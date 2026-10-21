using System.Text;

namespace SciBridge;

/// <summary>
/// One timestamped audit log per bridge session. It records operation names,
/// paths, byte counts and success/failure, never tokens, passwords or file data.
/// The folder is opened from the GUI so users can inspect any past session.
/// </summary>
public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public AuditLog(SessionInfo session)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AuditDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _path = System.IO.Path.Combine(AppPaths.AuditDir, $"audit-{stamp}-{session.Pid}.log");
            WriteRaw(Line("session",
                $"host={session.Host}:{session.Port} user={session.User} root={session.Root} " +
                $"upload={session.AllowUpload} mkdir={session.AllowMkdir}"));
        }
        catch
        {
            _path = "";
        }
    }

    public string Path => _path;

    public void Write(string operation, string path, string result)
    {
        if (_path.Length == 0) return;
        WriteRaw(Line(operation, $"path={Clean(path)} {Clean(result)}"));
    }

    private string Line(string operation, string detail)
        => $"{DateTimeOffset.Now:O}\t{operation}\t{Clean(detail)}{Environment.NewLine}";

    private void WriteRaw(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(AppPaths.AuditDir);
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Auditing must never break an operation.
        }
    }

    private static string Clean(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
