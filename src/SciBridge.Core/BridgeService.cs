namespace SciBridge;

/// <summary>A transport-independent request for one operation.</summary>
public sealed record BridgeRequest(
    string Op,
    string Path = ".",
    long Offset = 0,
    int Length = 0,
    string? Glob = null,
    int Max = 0,
    int Depth = 0,
    string Format = "text");

public abstract record BridgeResult
{
    public sealed record Payload(object Dto) : BridgeResult;
    public sealed record Raw(ReadResult Data, bool AsBase64, bool AsText) : BridgeResult;
    public sealed record Failure(string Message) : BridgeResult;
}

/// <summary>
/// Dispatches every read and (when enabled) write operation. This is the only
/// place that turns a request into remote work, so the surface stays auditable.
/// Write capability is deliberately limited to new files and new directories;
/// there is no overwrite, rename, delete or remote-exec operation.
/// </summary>
public sealed class BridgeService
{
    private static readonly string[] ReadOperationNames = { "health", "list", "stat", "read", "find" };

    private readonly RemoteFs _fs;
    private readonly SessionInfo _session;
    private readonly AuditLog _audit;
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    public BridgeService(RemoteFs fs, SessionInfo session, AuditLog audit)
    {
        _fs = fs;
        _session = session;
        _audit = audit;
    }

    public RemoteFs FileSystem => _fs;
    public bool CanUpload => _session.AllowUpload;
    public bool CanMkdir => _session.AllowMkdir;

    public BridgeResult Execute(BridgeRequest request)
    {
        _concurrency.Wait();
        try
        {
            BridgeResult result = request.Op switch
            {
                "health" => new BridgeResult.Payload(Health()),
                "list" => new BridgeResult.Payload(_fs.List(request.Path, request.Max)),
                "stat" => new BridgeResult.Payload(_fs.Stat(request.Path)),
                "find" => new BridgeResult.Payload(_fs.Find(request.Path, request.Glob ?? "", request.Max, request.Depth)),
                "read" => new BridgeResult.Raw(
                    _fs.Read(request.Path, request.Offset, request.Length),
                    AsBase64: request.Format == "b64" || request.Format == "json",
                    AsText: request.Format == "text"),
                _ => new BridgeResult.Failure(
                    $"不支持的操作 '{request.Op}'。支持：{string.Join(", ", SupportedOperations())}。"),
            };

            _audit.Write(request.Op, AuditTarget(request), Summary(result));
            return result;
        }
        catch (BridgeException ex)
        {
            _audit.Write(request.Op, AuditTarget(request), "error=" + ex.Message);
            return new BridgeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"op={request.Op} path={request.Path} failed: {ex}");
            var message = FriendlyFailure(ex);
            _audit.Write(request.Op, AuditTarget(request), "error=" + message);
            return new BridgeResult.Failure(message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>Uploads a brand-new file; existing files are never overwritten.</summary>
    public BridgeResult Upload(string path, byte[] data)
    {
        _concurrency.Wait();
        try
        {
            if (!CanUpload)
                return Reject("upload", path, "写入未开启：用户没有允许上传新文件。");

            var bytes = _fs.UploadNew(path, data);
            var dto = new WriteDto { Operation = "upload", Path = path, Bytes = bytes };
            _audit.Write("upload", path, $"ok bytes={bytes}");
            return new BridgeResult.Payload(dto);
        }
        catch (BridgeException ex)
        {
            _audit.Write("upload", path, "error=" + ex.Message);
            return new BridgeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"upload path={path} failed: {ex}");
            var message = FriendlyFailure(ex);
            _audit.Write("upload", path, "error=" + message);
            return new BridgeResult.Failure(message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>Creates one new directory; the parent must already exist.</summary>
    public BridgeResult Mkdir(string path)
    {
        _concurrency.Wait();
        try
        {
            if (!CanMkdir)
                return Reject("mkdir", path, "写入未开启：用户没有允许创建目录。");

            _fs.CreateDirectory(path);
            var dto = new WriteDto { Operation = "mkdir", Path = path, Bytes = 0 };
            _audit.Write("mkdir", path, "ok");
            return new BridgeResult.Payload(dto);
        }
        catch (BridgeException ex)
        {
            _audit.Write("mkdir", path, "error=" + ex.Message);
            return new BridgeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"mkdir path={path} failed: {ex}");
            var message = FriendlyFailure(ex);
            _audit.Write("mkdir", path, "error=" + message);
            return new BridgeResult.Failure(message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private BridgeResult Reject(string operation, string path, string message)
    {
        _audit.Write(operation, path, "error=" + message);
        return new BridgeResult.Failure(message);
    }

    private IEnumerable<string> SupportedOperations()
    {
        foreach (var op in ReadOperationNames) yield return op;
        if (CanUpload) yield return "upload";
        if (CanMkdir) yield return "mkdir";
    }

    private static string AuditTarget(BridgeRequest request)
    {
        var text = request.Path;
        if (request.Op == "read")
            text += $" offset={request.Offset} length={request.Length}";
        if (request.Op == "find" && !string.IsNullOrWhiteSpace(request.Glob))
            text += $" glob={request.Glob} max={request.Max} depth={request.Depth}";
        return text;
    }

    private static string Summary(BridgeResult result) => result switch
    {
        BridgeResult.Payload { Dto: ListDto list } =>
            $"ok count={list.Count} truncated={list.Truncated}",
        BridgeResult.Payload { Dto: FindDto find } =>
            $"ok count={find.Count} dirs={find.DirsVisited} truncated={find.Truncated}",
        BridgeResult.Payload { Dto: WriteDto write } =>
            $"ok bytes={write.Bytes}",
        BridgeResult.Payload => "ok",
        BridgeResult.Raw raw =>
            $"ok bytes={raw.Data.Data.Length} size={raw.Data.Size}",
        BridgeResult.Failure failure => "error=" + failure.Message,
        _ => "ok",
    };

    /// <summary>
    /// A real liveness probe: a cheap LSTAT of the access root proves that the
    /// SSH session and the approved root are both usable.
    /// </summary>
    private HealthDto Health()
    {
        _fs.Stat(".");

        var writeOperations = new List<string>();
        if (_session.AllowUpload) writeOperations.Add("upload");
        if (_session.AllowMkdir) writeOperations.Add("mkdir");

        return new HealthDto
        {
            Version = _session.AppVersion,
            Host = _session.Host,
            Port = _session.Port,
            User = _session.User,
            Root = _fs.Root,
            Home = _fs.Home,
            ReadOnly = writeOperations.Count == 0,
            RootReachable = true,
            AllowSymlinks = _fs.AllowSymlinks,
            AllowUpload = _session.AllowUpload,
            AllowMkdir = _session.AllowMkdir,
            Operations = ReadOperationNames.ToList(),
            WriteOperations = writeOperations,
        };
    }

    private static string FriendlyFailure(Exception ex) => ex switch
    {
        Renci.SshNet.Common.SshOperationTimeoutException =>
            "服务器响应超时。数据可能过大或网络不稳定，请缩小请求范围后重试。",
        Renci.SshNet.Common.SshConnectionException =>
            "与服务器的连接已中断。请让用户在本机 SciBridge 窗口中重新连接。",
        UnauthorizedAccessException or Renci.SshNet.Common.SftpPermissionDeniedException =>
            "服务器拒绝访问该路径（权限不足）。",
        _ => ex.Message,
    };

    public void Dispose() => _concurrency.Dispose();
}
