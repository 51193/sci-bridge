using System.Security.Cryptography;

namespace SciBridge;

/// <summary>
/// Owns one live read-only bridge: the loopback HTTP server plus the session
/// descriptor shared with the GUI (and with the optional self-test).
/// There is deliberately no second transport and no on-disk session file.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    private readonly HttpBridgeServer _http;
    private readonly BridgeService _service;
    private bool _disposed;

    private BridgeHost(BridgeService service, HttpBridgeServer http, SessionInfo session)
    {
        _service = service;
        _http = http;
        Session = session;
    }

    public SessionInfo Session { get; }

    public static BridgeHost Start(RemoteFs fs, string host, int port, string user,
        bool allowUpload = false, bool allowMkdir = false)
    {
        var token = NewToken();
        var session = new SessionInfo
        {
            Pid = Environment.ProcessId,
            Host = host,
            Port = port,
            User = user,
            Root = fs.Root,
            Home = fs.Home,
            Token = token,
            AppVersion = BuildInfo.Version,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            AllowSymlinks = fs.AllowSymlinks,
            AllowUpload = allowUpload,
            AllowMkdir = allowMkdir,
        };

        var audit = new AuditLog(session);
        var service = new BridgeService(fs, session, audit);
        var http = new HttpBridgeServer(service, token);
        session.Url = $"http://127.0.0.1:{http.Port}";

        http.Start();

        Log.Write($"bridge started url={session.Url} pid={session.Pid}");
        return new BridgeHost(service, http, session);
    }

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _http.Dispose(); } catch { /* best effort */ }
        try { _service.Dispose(); } catch { /* best effort */ }
        Log.Write("bridge stopped");
    }
}
