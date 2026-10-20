using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SciBridge;

/// <summary>
/// A deliberately small HTTP/1.1 server bound to 127.0.0.1 only.
/// HttpListener is avoided on purpose: it needs an http.sys URL reservation
/// (administrator rights) on Windows, which would wreck one-click deployment.
/// </summary>
public sealed class HttpBridgeServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TcpListener _listener;
    private readonly BridgeService _service;
    private readonly byte[] _tokenBytes;
    private readonly CancellationTokenSource _cts = new();

    public HttpBridgeServer(BridgeService service, string token)
    {
        _service = service;
        _tokenBytes = Encoding.UTF8.GetBytes(token);

        // Loopback only. Never IPAddress.Any: that would expose the data to the LAN
        // and would trigger a Windows Firewall prompt.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var ct = timeout.Token;

                var head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
                if (head is null) return;

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    await WriteAsync(stream, 400, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("error: 请求格式无效。\n"), ct).ConfigureAwait(false);
                    return;
                }

                var method = requestLine[0];
                var target = requestLine[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var idx = lines[i].IndexOf(':');
                    if (idx <= 0) continue;
                    headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
                }

                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    await WriteAsync(stream, 405, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("error: 只支持 GET。本工具是只读的。\n"), ct).ConfigureAwait(false);
                    return;
                }

                // Reject anything a web page could have triggered, so a malicious site
                // cannot use the user's browser to probe the bridge.
                if (headers.ContainsKey("Origin") || headers.ContainsKey("Referer") ||
                    headers.ContainsKey("Sec-Fetch-Mode") || headers.ContainsKey("Sec-Fetch-Site"))
                {
                    await WriteAsync(stream, 403, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("error: 已拒绝来自浏览器上下文的请求。\n"), ct).ConfigureAwait(false);
                    return;
                }

                var queryIndex = target.IndexOf('?');
                var pathPart = queryIndex < 0 ? target : target[..queryIndex];
                var queryPart = queryIndex < 0 ? "" : target[(queryIndex + 1)..];

                var segments = pathPart.Trim('/').Split('/');
                if (segments.Length < 2 || segments[0].Length == 0)
                {
                    await WriteAsync(stream, 404, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("error: 路径应为 /<token>/<operation>。\n"), ct).ConfigureAwait(false);
                    return;
                }

                var supplied = Encoding.UTF8.GetBytes(segments[0]);
                if (!CryptographicOperations.FixedTimeEquals(supplied, _tokenBytes))
                {
                    await WriteAsync(stream, 401, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("error: 令牌无效。请使用本机 SciBridge 窗口显示的当前连接信息。\n"), ct)
                        .ConfigureAwait(false);
                    return;
                }

                var query = ParseQuery(queryPart);
                var operation = segments[1].ToLowerInvariant();
                var request = new BridgeRequest(
                    Op: operation,
                    Path: query.GetValueOrDefault("path", "."),
                    Offset: ParseLong(query.GetValueOrDefault("offset"), 0),
                    Length: ParseInt(query.GetValueOrDefault("length"), 0),
                    Glob: query.GetValueOrDefault("glob"),
                    Max: ParseInt(query.GetValueOrDefault("max"), 0),
                    Depth: ParseInt(query.GetValueOrDefault("depth"), 0),
                    // A raw read must never be text-decoded by default: that would corrupt binary data.
                    Format: query.GetValueOrDefault("format", operation == "read" ? "raw" : "text"));

                var result = _service.Execute(request);
                await RespondAsync(stream, result, request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("http handler failed: " + ex.Message);
            }
        }
    }

    private static async Task RespondAsync(
        NetworkStream stream, BridgeResult result, BridgeRequest request, CancellationToken ct)
    {
        switch (result)
        {
            case BridgeResult.Failure failure:
                await WriteAsync(stream, 400, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("error: " + failure.Message + "\n"), ct).ConfigureAwait(false);
                return;

            case BridgeResult.Raw raw:
                if (raw.AsText)
                {
                    var text = Encoding.UTF8.GetString(raw.Data.Data);
                    await WriteAsync(stream, 200, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(text), ct, ReadHeaders(raw.Data)).ConfigureAwait(false);
                }
                else if (raw.AsBase64)
                {
                    var dto = new ReadDto
                    {
                        Path = raw.Data.Path,
                        Offset = raw.Data.Offset,
                        Size = raw.Data.Size,
                        Bytes = raw.Data.Data.Length,
                        Eof = raw.Data.Offset + raw.Data.Data.Length >= raw.Data.Size,
                        Encoding = "base64",
                        Data = Convert.ToBase64String(raw.Data.Data),
                    };
                    await WriteAsync(stream, 200, "application/json; charset=utf-8",
                        JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions), ct, ReadHeaders(raw.Data))
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteAsync(stream, 200, "application/octet-stream",
                        raw.Data.Data, ct, ReadHeaders(raw.Data)).ConfigureAwait(false);
                }
                return;

            case BridgeResult.Payload payload:
                if (payload.Dto is HealthDto health) health.Transport = "http-loopback";

                var wantsJson = string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase);
                var body = wantsJson
                    ? JsonSerializer.SerializeToUtf8Bytes(payload.Dto, JsonOptions)
                    : Encoding.UTF8.GetBytes(TextRenderer.Render(result));
                var contentType = wantsJson
                    ? "application/json; charset=utf-8"
                    : "text/plain; charset=utf-8";

                await WriteAsync(stream, 200, contentType, body, ct).ConfigureAwait(false);
                return;
        }
    }

    private static Dictionary<string, string> ReadHeaders(ReadResult data) => new()
    {
        ["X-SciBridge-Path"] = Uri.EscapeDataString(data.Path),
        ["X-SciBridge-Offset"] = data.Offset.ToString(),
        ["X-SciBridge-Size"] = data.Size.ToString(),
        ["X-SciBridge-Bytes"] = data.Data.Length.ToString(),
    };

    private static async Task WriteAsync(
        NetworkStream stream, int status, string contentType, byte[] body,
        CancellationToken ct, Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("X-SciBridge-ReadOnly: true\r\n");
        sb.Append("Connection: close\r\n");
        if (extra is not null)
        {
            foreach (var (key, value) in extra) sb.Append(key).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Error",
    };

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, 1), ct).ConfigureAwait(false);
            if (read == 0) return total == 0 ? null : Encoding.ASCII.GetString(buffer, 0, total);
            total += read;
            if (total >= 4 && buffer[total - 4] == (byte)'\r' && buffer[total - 3] == (byte)'\n' &&
                buffer[total - 2] == (byte)'\r' && buffer[total - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, total);
            }
        }
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var value = idx < 0 ? "" : pair[(idx + 1)..];
            // '+' stays literal because filenames may contain it. Percent-encode spaces as %20.
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private static long ParseLong(string? value, long fallback)
        => long.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}
