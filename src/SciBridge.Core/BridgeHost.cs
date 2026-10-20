using System.Diagnostics;
using System.Security.Cryptography;

namespace SciBridge;

/// <summary>
/// Owns one live read-only bridge: the two transports plus the on-disk session
/// descriptor that the CLI uses to find them.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    private readonly HttpBridgeServer _http;
    private readonly PipeBridgeServer _pipe;
    private readonly BridgeService _service;
    private readonly string _sessionPath;
    private bool _disposed;

    private BridgeHost(BridgeService service, HttpBridgeServer http, PipeBridgeServer pipe,
        SessionInfo session, string sessionPath)
    {
        _service = service;
        _http = http;
        _pipe = pipe;
        Session = session;
        _sessionPath = sessionPath;
    }

    public SessionInfo Session { get; }
    public string SessionPath => _sessionPath;

    public static BridgeHost Start(RemoteFs fs, string host, int port, string user,
        string? sessionPath = null, string? exePathOverride = null)
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
            Pipe = "scibridge-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            AllowSymlinks = fs.AllowSymlinks,
            ExePath = exePathOverride ?? Environment.ProcessPath ?? "",
        };

        var service = new BridgeService(fs, session);
        var http = new HttpBridgeServer(service, token);
        session.Url = $"http://127.0.0.1:{http.Port}";

        var pipe = new PipeBridgeServer(service, session.Pipe);

        var path = sessionPath ?? AppPaths.SessionFile;
        session.SaveAtomic(path);

        http.Start();
        pipe.Start();

        Log.Write($"bridge started url={session.Url} pipe={session.Pipe} pid={session.Pid}");
        return new BridgeHost(service, http, pipe, session, path);
    }

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Cheap liveness probe used by the CLI before reporting a broken bridge.</summary>
    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _http.Dispose(); } catch { /* best effort */ }
        try { _pipe.Dispose(); } catch { /* best effort */ }
        try { _service.Dispose(); } catch { /* best effort */ }
        Session.DeleteIfOwned(_sessionPath);
        Log.Write("bridge stopped");
    }
}
