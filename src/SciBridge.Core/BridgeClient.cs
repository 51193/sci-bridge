using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SciBridge;

/// <summary>
/// Talks to a running <see cref="BridgeHost"/> from another process. It never needs
/// the SSH password: the bridge already holds the authenticated session.
/// HTTP over loopback is tried first; if that is blocked (some sandboxes deny loopback
/// TCP) the client transparently falls back to the named pipe.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private enum Transport { Unknown, Http, Pipe }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    private readonly SessionInfo _session;
    private readonly HttpClient _http;
    private Transport _transport = Transport.Unknown;

    private BridgeClient(SessionInfo session)
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
    public string TransportUsed => _transport switch
    {
        Transport.Http => "http-loopback",
        Transport.Pipe => "named-pipe",
        _ => "unknown",
    };

    public static BridgeClient Create(string? explicitSessionPath = null, string? outPath = null)
    {
        foreach (var candidate in AppPaths.SessionFileCandidates(explicitSessionPath))
        {
            var info = SessionInfo.TryLoad(candidate);
            if (info is null) continue;
            outPath = candidate;
            return new BridgeClient(info);
        }

        throw new BridgeException(
            "未找到连接信息。请让用户先双击 SciBridge.exe，填好服务器信息并点击“连接”，" +
            "窗口显示“已连接”后再重试。");
    }

    /// <summary>Runs one read-only operation and returns the matching DTO.</summary>
    public object Call(BridgeRequest request)
    {
        Exception? httpError = null;

        if (_transport is Transport.Unknown or Transport.Http)
        {
            try
            {
                var result = FromJson(CallHttp(request), request.Op);
                _transport = Transport.Http;
                return result;
            }
            catch (BridgeException)
            {
                throw; // A real operation error, not a transport problem.
            }
            catch (Exception ex)
            {
                httpError = ex;
                if (_transport == Transport.Http) throw Describe(ex);
            }
        }

        try
        {
            var result = FromJson(CallPipe(request), request.Op);
            _transport = Transport.Pipe;
            return result;
        }
        catch (BridgeException)
        {
            throw;
        }
        catch (Exception pipeError)
        {
            throw new BridgeException(BuildFailureMessage(httpError, pipeError));
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

    // ------------------------------------------------------------------ HTTP

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
            throw new BridgeException("连接信息缺少本机地址，可能来自旧版本。请让用户重新连接。");

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

    // ------------------------------------------------------------------ pipe

    private JsonElement CallPipe(BridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(_session.Pipe))
            throw new BridgeException("连接信息缺少管道名称。请让用户重新连接。");

        using var pipe = new NamedPipeClientStream(".", _session.Pipe, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(5000);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var envelope = new PipeEnvelope
        {
            Op = request.Op,
            Path = request.Path,
            Offset = request.Offset,
            Length = request.Length,
            Glob = request.Glob,
            Max = request.Max,
            Depth = request.Depth,
            Format = "json",
        };

        writer.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));

        var line = reader.ReadLine();
        if (line is null) throw new BridgeException("命名管道没有返回数据。");

        var response = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions)
            ?? throw new BridgeException("命名管道返回了无法解析的数据。");

        if (response.Ok != true)
            throw new BridgeException(response.Error ?? "请求失败。");

        if (response.Result is null)
            throw new BridgeException("命名管道返回了空结果。");

        return response.Result.Value;
    }

    // --------------------------------------------------------------- errors

    private BridgeException Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: SocketException se } =>
            new BridgeException($"无法连接本机桥（{se.SocketErrorCode}）。"),
        TaskCanceledException or OperationCanceledException =>
            new BridgeException("请求超时。数据可能过大，请缩小范围后重试。"),
        _ => new BridgeException(ex.Message),
    };

    private string BuildFailureMessage(Exception? httpError, Exception pipeError)
    {
        var detail = $"回环 HTTP：{Short(httpError)}；命名管道：{Short(pipeError)}";
        return "无法连接到正在运行的 SciBridge（" + detail + "）。" +
               "请让用户确认 SciBridge 窗口仍打开并显示“已连接”；" +
               "若窗口正常但仍失败，可能是当前沙箱禁止了本机回环网络，请让用户按提示授予本机网络访问权限。";
    }

    private static string Short(Exception? ex) => ex switch
    {
        null => "未尝试",
        HttpRequestException { InnerException: SocketException se } => se.SocketErrorCode.ToString(),
        SocketException se => se.SocketErrorCode.ToString(),
        TimeoutException => "超时",
        IOException io => io.Message,
        _ => ex.GetType().Name,
    };

    public void Dispose() => _http.Dispose();
}
