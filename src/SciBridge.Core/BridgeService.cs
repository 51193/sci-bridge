namespace SciBridge;

/// <summary>A transport-independent request for one read-only operation.</summary>
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
/// Dispatches the five read-only operations. This is the only place that turns a
/// request into remote work, so the read-only surface is auditable in one screen.
/// </summary>
public sealed class BridgeService
{
    private static readonly string[] OperationNames = { "health", "list", "stat", "read", "find" };

    private readonly RemoteFs _fs;
    private readonly SessionInfo _session;
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    public BridgeService(RemoteFs fs, SessionInfo session)
    {
        _fs = fs;
        _session = session;
    }

    public RemoteFs FileSystem => _fs;

    public BridgeResult Execute(BridgeRequest request)
    {
        _concurrency.Wait();
        try
        {
            return request.Op switch
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
                    $"不支持的操作 '{request.Op}'。本工具只提供只读操作：{string.Join(", ", OperationNames)}。"),
            };
        }
        catch (BridgeException ex)
        {
            return new BridgeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"op={request.Op} path={request.Path} failed: {ex}");
            return new BridgeResult.Failure(FriendlyFailure(ex));
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// A real liveness probe, not just a description of the process: a cheap
    /// LSTAT of the access root proves that the SSH session and the approved
    /// root are both usable. A dead SSH connection therefore fails health.
    /// </summary>
    private HealthDto Health()
    {
        _fs.Stat(".");

        return new HealthDto
        {
            Version = _session.AppVersion,
            Host = _session.Host,
            Port = _session.Port,
            User = _session.User,
            Root = _fs.Root,
            Home = _fs.Home,
            ReadOnly = true,
            RootReachable = true,
            AllowSymlinks = _fs.AllowSymlinks,
            Operations = OperationNames.ToList(),
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
