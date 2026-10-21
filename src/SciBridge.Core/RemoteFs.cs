using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SciBridge;

public sealed record ReadResult(string Path, long Offset, long Size, byte[] Data);

internal readonly record struct Resolved(string RemotePath, string Name, SftpFileAttributes Attributes, bool IsSymlink);

/// <summary>
/// Every remote operation the tool can perform. There is deliberately no write,
/// rename, delete, chmod or exec member anywhere in this type, and every path is
/// first confined to <see cref="Root"/>.
///
/// Note on symlinks: SSH.NET's GetAttributes issues LSTAT, so attributes never
/// follow a link. That is what makes the default "inspect but never traverse a
/// symlink" policy enforceable without a server-side realpath call.
/// </summary>
public sealed class RemoteFs : IDisposable
{
    public const int DefaultReadLength = 256 * 1024;
    public const int MaxReadLength = 4 * 1024 * 1024;
    public const int DefaultListMax = 2000;
    public const int MaxListMax = 20000;
    public const int DefaultFindMax = 500;
    public const int MaxFindMax = 5000;
    public const int DefaultFindDepth = 5;
    public const int MaxFindDepth = 12;
    public const int MaxFindDirs = 20000;
    public const int MaxFindSeconds = 45;

    private readonly SftpClient _sftp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime At, List<ISftpFile> Entries)> _dirCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private const int CacheCapacity = 64;

    public RemoteFs(SftpClient sftp, string root, bool allowSymlinks)
    {
        _sftp = sftp;
        Root = PathGuard.NormalizeRoot(root);
        AllowSymlinks = allowSymlinks;
        Home = SafeWorkingDirectory();
    }

    public string Root { get; }
    public string Home { get; }
    public bool AllowSymlinks { get; }

    private string SafeWorkingDirectory()
    {
        try { return _sftp.WorkingDirectory ?? ""; }
        catch { return ""; }
    }

    private T Run<T>(Func<T> action)
    {
        _gate.Wait();
        try
        {
            if (!_sftp.IsConnected)
                throw new BridgeException("与服务器的连接已断开。请让用户在本机 SciBridge 窗口中重新连接。");
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BridgeException SymlinkRefused(string name) => new(
        $"'{name}' 是符号链接。只读策略默认拒绝跟随符号链接，以免越出获准范围。" +
        "若确需访问，请让用户在本机 SciBridge 窗口中勾选“允许跟随符号链接”后重新连接。");

    // ---------------------------------------------------------------- listing

    public ListDto List(string agentPath, int max)
    {
        var relative = PathGuard.NormalizeRelative(agentPath);
        max = Math.Clamp(max <= 0 ? DefaultListMax : max, 1, MaxListMax);

        return Run(() =>
        {
            var target = Resolve(relative);
            if (target.IsSymlink && !AllowSymlinks) throw SymlinkRefused(target.Name);
            if (!target.IsSymlink && !target.Attributes.IsDirectory)
                throw new BridgeException($"'{relative}' 不是目录。");

            List<ISftpFile> files;
            try
            {
                files = _sftp.ListDirectory(target.RemotePath).ToList();
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException($"'{relative}' 不是可访问的目录。", ex);
            }

            var entries = new List<EntryDto>();
            var truncated = false;

            foreach (var file in files)
            {
                if (file.Name is "." or "..") continue;
                if (entries.Count >= max) { truncated = true; break; }
                entries.Add(ToEntry(file.Name, file.Attributes, file.IsSymbolicLink));
            }

            return new ListDto
            {
                Path = relative,
                Count = entries.Count,
                Truncated = truncated,
                Entries = entries,
            };
        });
    }

    // ------------------------------------------------------------------- stat

    /// <summary>
    /// Reports the entry itself. A symlink is reported as a link rather than
    /// silently resolved, so an agent can see what it is without following it.
    /// </summary>
    public StatDto Stat(string agentPath)
    {
        var relative = PathGuard.NormalizeRelative(agentPath);
        return Run(() =>
        {
            var target = Resolve(relative);
            return new StatDto
            {
                Path = relative,
                Entry = ToEntry(target.Name, target.Attributes, target.IsSymlink),
            };
        });
    }

    // ------------------------------------------------------------------- read

    public ReadResult Read(string agentPath, long offset, int length)
    {
        var relative = PathGuard.NormalizeRelative(agentPath);
        if (relative == ".")
            throw new BridgeException("根目录本身不是文件。请指定具体文件名。");
        if (offset < 0)
            throw new BridgeException("offset 必须是非负整数。");
        length = length <= 0 ? DefaultReadLength : Math.Min(length, MaxReadLength);

        return Run(() =>
        {
            var target = Resolve(relative);
            if (target.IsSymlink && !AllowSymlinks) throw SymlinkRefused(target.Name);
            if (!target.IsSymlink && target.Attributes.IsDirectory)
                throw new BridgeException($"'{relative}' 是目录，不能直接读取。请先列出目录内容。");

            SftpFileStream stream;
            try
            {
                stream = _sftp.OpenRead(target.RemotePath);
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException($"'{relative}' 不是可读取的普通文件。", ex);
            }

            using (stream)
            {
                // LSTAT cannot report a link target's size, so trust the opened handle for links.
                var size = target.IsSymlink ? stream.Length : target.Attributes.Size;
                if (offset >= size)
                    return new ReadResult(relative, offset, size, Array.Empty<byte>());

                if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[Math.Min(length, size - offset)];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0) break;
                    total += read;
                }
                if (total != buffer.Length) Array.Resize(ref buffer, total);

                return new ReadResult(relative, offset, size, buffer);
            }
        });
    }

    // ------------------------------------------------------------------- find

    /// <summary>
    /// Bounded breadth-first name search. Never recursive without limits, so an
    /// agent cannot accidentally walk an entire multi-terabyte dataset.
    /// <paramref name="depth"/> counts directory levels below the search root
    /// (the root itself is level 0).
    /// </summary>
    public FindDto Find(string agentPath, string glob, int max, int depth)
    {
        var relative = PathGuard.NormalizeRelative(agentPath);
        if (string.IsNullOrWhiteSpace(glob)) throw new BridgeException("find 需要一个 glob 参数，例如 *.nc。");
        if (glob.Length > 256) throw new BridgeException("glob 过长。");
        if (glob.Contains("..")) throw new BridgeException("glob 中不允许包含 '..'。");

        max = Math.Clamp(max <= 0 ? DefaultFindMax : max, 1, MaxFindMax);
        depth = Math.Clamp(depth <= 0 ? DefaultFindDepth : depth, 1, MaxFindDepth);

        var pathMode = glob.Contains('/');
        var regex = GlobToRegex(glob);
        var deadline = DateTime.UtcNow.AddSeconds(MaxFindSeconds);

        return Run(() =>
        {
            var start = Resolve(relative);
            if (start.IsSymlink && !AllowSymlinks) throw SymlinkRefused(start.Name);

            var result = new FindDto { Path = relative, Glob = glob };
            var queue = new Queue<(string RemotePath, string Relative, int Depth)>();
            queue.Enqueue((start.RemotePath, relative, 0));

            while (queue.Count > 0)
            {
                if (result.Entries.Count >= max) { result.Truncated = true; break; }
                if (result.DirsVisited >= MaxFindDirs || DateTime.UtcNow > deadline) { result.Truncated = true; break; }

                var (remoteDir, relDir, currentDepth) = queue.Dequeue();
                result.DirsVisited++;

                List<ISftpFile> files;
                try
                {
                    files = _sftp.ListDirectory(remoteDir).ToList();
                }
                catch (Exception ex) when (ex is not BridgeException && _sftp.IsConnected)
                {
                    continue; // An unreadable subdirectory is skipped, not fatal, for a search.
                }

                foreach (var file in files)
                {
                    if (file.Name is "." or "..") continue;
                    if (result.Entries.Count >= max) { result.Truncated = true; break; }
                    if (file.IsSymbolicLink && !AllowSymlinks) continue;

                    var childRelative = relDir == "." ? file.Name : relDir + "/" + file.Name;
                    var candidate = pathMode ? childRelative : file.Name;

                    if (regex.IsMatch(candidate))
                    {
                        var entry = ToEntry(file.Name, file.Attributes, file.IsSymbolicLink);
                        entry.Name = childRelative; // find reports the location, not just the leaf
                        result.Entries.Add(entry);
                    }

                    if (file.IsDirectory && currentDepth < depth)
                        queue.Enqueue((remoteDir.TrimEnd('/') + "/" + file.Name, childRelative, currentDepth + 1));
                }
            }

            result.Count = result.Entries.Count;
            return result;
        });
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Uploads a brand-new file. Existing paths are never overwritten: the
    /// caller must choose a new name. The bytes go to a temporary file in the
    /// same directory and are renamed only after the upload succeeds.
    /// </summary>
    public long UploadNew(string agentPath, byte[] data)
    {
        var relative = NormalizeWritePath(agentPath);

        return Run(() =>
        {
            var target = ResolveNewPath(relative);
            var temp = JoinRemote(target.ParentRemote, ".scibridge-upload-" + Guid.NewGuid().ToString("N"));

            try
            {
                using var input = new MemoryStream(data, writable: false);
                _sftp.UploadFile(input, temp, canOverride: false);

                // Re-check immediately before the rename: no overwrite is allowed.
                _dirCache.Clear();
                if (ListParent(target.ParentRemote).Any(f => f.Name == target.Name))
                    throw new BridgeException($"路径已存在：'{relative}'。本工具只允许新建，不能覆盖。");

                _sftp.RenameFile(temp, target.RemotePath);
                _dirCache.Clear();
                return (long)data.Length;
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException($"上传失败：'{relative}'。", ex);
            }
            finally
            {
                try { if (_sftp.Exists(temp)) _sftp.DeleteFile(temp); }
                catch { /* best effort */ }
            }
        });
    }

    /// <summary>Creates one directory; the parent must already exist.</summary>
    public void CreateDirectory(string agentPath)
    {
        var relative = NormalizeWritePath(agentPath);

        Run(() =>
        {
            var target = ResolveNewPath(relative);
            try
            {
                _sftp.CreateDirectory(target.RemotePath);
                _dirCache.Clear();
                return true;
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException($"创建目录失败：'{relative}'。", ex);
            }
        });
    }

    private static string NormalizeWritePath(string? agentPath)
    {
        var relative = PathGuard.NormalizeRelative(agentPath);
        if (relative == ".")
            throw new BridgeException("根目录本身不能作为写入目标。");
        return relative;
    }

    private (string RemotePath, string ParentRemote, string Name) ResolveNewPath(string relative)
    {
        var components = PathGuard.Components(relative);
        var name = components[^1];
        var parentRelative = components.Count == 1
            ? "."
            : string.Join('/', components.Take(components.Count - 1));

        var parent = Resolve(parentRelative);
        if (parent.IsSymlink) throw SymlinkRefused(parent.Name);
        if (!parent.Attributes.IsDirectory)
            throw new BridgeException($"'{parentRelative}' 不是目录。");

        var remote = JoinRemote(parent.RemotePath, name);
        if (ListParent(parent.RemotePath).Any(f => f.Name == name))
            throw new BridgeException($"路径已存在：'{relative}'。本工具只允许新建，不能覆盖。");

        return (remote, parent.RemotePath, name);
    }

    private static string JoinRemote(string parent, string name)
        => parent == "." ? name : parent.TrimEnd('/') + "/" + name;

    public static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // -------------------------------------------------------------- resolution

    /// <summary>
    /// Resolves a root-relative path against the root. An intermediate symlink is
    /// always refused unless the user opted in, because traversing one is exactly
    /// how a read could escape the approved scope. A final component that is a
    /// symlink is returned as-is so callers can inspect it, and each caller decides
    /// whether following it would be safe.
    /// </summary>
    private Resolved Resolve(string relative)
    {
        var components = PathGuard.Components(relative);
        if (components.Count == 0)
        {
            var rootAttributes = _sftp.GetAttributes(Root);
            return new Resolved(Root, Root == "." ? "." : LastSegment(Root), rootAttributes, false);
        }

        var currentRemote = Root;
        var currentRelative = ".";
        ISftpFile? current = null;

        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            var isLast = i == components.Count - 1;

            List<ISftpFile> listing;
            try
            {
                listing = ListParent(currentRemote);
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException(
                    $"'{currentRelative}' 不是可访问的目录，无法继续解析路径。", ex);
            }

            var match = listing.FirstOrDefault(f => string.Equals(f.Name, component, StringComparison.Ordinal));
            if (match is null)
                throw new BridgeException($"路径不存在：'{JoinRelative(currentRelative, component)}'。");

            if (match.IsSymbolicLink && !AllowSymlinks && !isLast)
                throw SymlinkRefused(match.Name);

            currentRemote = currentRemote == "." ? component : currentRemote.TrimEnd('/') + "/" + component;
            currentRelative = JoinRelative(currentRelative, component);
            current = match;
        }

        return new Resolved(currentRemote, current!.Name, current.Attributes, current.IsSymbolicLink);
    }

    private static string JoinRelative(string parent, string child)
        => parent == "." ? child : parent + "/" + child;

    private static string LastSegment(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    private List<ISftpFile> ListParent(string remoteDir)
    {
        if (_dirCache.TryGetValue(remoteDir, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Entries;

        var entries = new List<ISftpFile>();
        foreach (var file in _sftp.ListDirectory(remoteDir))
        {
            if (file.Name is "." or "..") continue;
            entries.Add(file);
        }

        if (_dirCache.Count >= CacheCapacity) _dirCache.Clear();
        _dirCache[remoteDir] = (DateTime.UtcNow, entries);
        return entries;
    }

    // --------------------------------------------------------------- mapping

    private static EntryDto ToEntry(string name, SftpFileAttributes attributes, bool symlink)
    {
        string type;
        if (symlink) type = "link";
        else if (attributes.IsDirectory) type = "dir";
        else if (attributes.IsRegularFile) type = "file";
        else type = "other";

        return new EntryDto
        {
            Name = name,
            Type = type,
            Size = attributes.Size,
            Mtime = attributes.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Mode = Permissions(attributes),
            Symlink = symlink,
        };
    }

    private static string Permissions(SftpFileAttributes a)
    {
        Span<char> mode = stackalloc char[9];
        mode[0] = a.OwnerCanRead ? 'r' : '-';
        mode[1] = a.OwnerCanWrite ? 'w' : '-';
        mode[2] = a.OwnerCanExecute ? 'x' : '-';
        mode[3] = a.GroupCanRead ? 'r' : '-';
        mode[4] = a.GroupCanWrite ? 'w' : '-';
        mode[5] = a.GroupCanExecute ? 'x' : '-';
        mode[6] = a.OthersCanRead ? 'r' : '-';
        mode[7] = a.OthersCanWrite ? 'w' : '-';
        mode[8] = a.OthersCanExecute ? 'x' : '-';
        return new string(mode);
    }

    public void Dispose()
    {
        try { _sftp.Dispose(); } catch { /* best effort */ }
        _gate.Dispose();
    }
}
