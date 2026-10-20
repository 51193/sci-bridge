using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SciBridge;

/// <summary>
/// Small HTTP client used by the optional self-test.
/// It is deliberately not the agent-facing interface: agents use their own
/// local HTTP client (curl.exe) against the same loopback endpoint.
/// There is no named-pipe fallback and no session-file discovery.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    private readonly SessionInfo _session;
    private readonly HttpClient _http;

    public BridgeClient(SessionInfo session)
    {
        _session = session;
        // No proxy: the bearer token must never be forwarded to anything else.
        var handler = new SocketsHttpHandler
        {
            Proxy = null,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public SessionInfo Session => _session;

    /// <summary>Runs one read-only operation and returns the matching DTO.</summary>
    public object Call(BridgeRequest request)
    {
        try
        {
            return FromJson(CallHttp(request), request.Op);
        }
        catch (BridgeException)
        {
            throw; // A real operation error, not a transport problem.
        }
        catch (Exception ex)
        {
            throw Describe(ex);
        }
    }

    private static object FromJson(JsonElement element, string op) => op switch
    {
        "health" => element.Deserialize<HealthDto>(JsonOptions)!,
        "list" => element.Deserialize<ListDto>(JsonOptions)!,
        "stat" => element.Deserialize<StatDto>(JsonOptions)!,
        "find" => element.Deserialize<FindDto>(JsonOptions)!,
        "read" => element.Deserialize<ReadDto>(JsonOptions)!,
        _ => throw new BridgeException($"不支持的操作 '{op}'。"),
    };

    private JsonElement CallHttp(BridgeRequest request)
    {
        var url = BuildUrl(request);
        using var response = _http.GetAsync(url).GetAwaiter().GetResult();
        var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            throw new BridgeException(ExtractError(body));

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private string BuildUrl(BridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(_session.Url))
            throw new BridgeException("连接信息缺少本机地址。请重新连接。");

        // Defensive: only ever talk to loopback over plain HTTP.
        if (!Uri.TryCreate(_session.Url, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(baseUri.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new BridgeException($"连接地址不是本机回环地址，已停止：{_session.Url}");
        }

        var query = new List<string> { "path=" + Uri.EscapeDataString(request.Path) };
        if (request.Op == "read")
        {
            query.Add("offset=" + request.Offset);
            query.Add("length=" + request.Length);
        }
        if (request.Op == "find")
        {
            query.Add("glob=" + Uri.EscapeDataString(request.Glob ?? ""));
            if (request.Max > 0) query.Add("max=" + request.Max);
            if (request.Depth > 0) query.Add("depth=" + request.Depth);
        }
        if (request.Op == "list" && request.Max > 0) query.Add("max=" + request.Max);
        query.Add("format=json");

        return $"{_session.Url.TrimEnd('/')}/{_session.Token}/{request.Op}?{string.Join('&', query)}";
    }

    private static string ExtractError(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        const string prefix = "error:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(text) ? "请求失败。" : text;
    }

    private BridgeException Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: SocketException se } =>
            new BridgeException(
                $"无法连接本机 SciBridge 的 HTTP 接口（{se.SocketErrorCode}）。" +
                "请确认 SciBridge 窗口仍打开并显示“已连接”；" +
                "若当前沙箱禁止本机回环网络，请向用户申请本机联网权限。"),
        TaskCanceledException or OperationCanceledException =>
            new BridgeException("请求超时。数据可能过大，请缩小范围后重试。"),
        _ => new BridgeException(ex.Message),
    };

    public void Dispose() => _http.Dispose();
}
