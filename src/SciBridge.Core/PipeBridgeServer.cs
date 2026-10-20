using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SciBridge;

/// <summary>
/// Named-pipe transport. It exists because a sandbox that blocks loopback TCP can
/// still permit pipe access, so the CLI keeps working when port access is denied.
/// The wire format is one JSON object per line; binary payloads travel as base64.
/// </summary>
public sealed class PipeBridgeServer : IDisposable
{
    private const int MaxRequestBytes = 64 * 1024;

    private readonly string _pipeName;
    private readonly BridgeService _service;
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public PipeBridgeServer(BridgeService service, string pipeName)
    {
        _service = service;
        _pipeName = pipeName;
    }

    public string PipeName => _pipeName;

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                Log.Write("pipe create failed: " + ex.Message);
                return;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Write("pipe wait failed: " + ex.Message);
                server.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(server), CancellationToken.None);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            try
            {
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) return;
                if (line.Length > MaxRequestBytes) return;

                PipeEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    await writer.WriteLineAsync(Serialize(new PipeEnvelope
                    {
                        Ok = false,
                        Error = "请求不是合法的 JSON。",
                    })).ConfigureAwait(false);
                    return;
                }

                if (envelope is null) return;

                var request = new BridgeRequest(
                    Op: (envelope.Op ?? "").ToLowerInvariant(),
                    Path: envelope.Path,
                    Offset: envelope.Offset,
                    Length: envelope.Length,
                    Glob: envelope.Glob,
                    Max: envelope.Max,
                    Depth: envelope.Depth,
                    Format: envelope.Format ?? "json");

                var result = _service.Execute(request);
                await writer.WriteLineAsync(Serialize(ToEnvelope(result))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("pipe handler failed: " + ex.Message);
            }
        }
    }

    private static PipeEnvelope ToEnvelope(BridgeResult result)
    {
        switch (result)
        {
            case BridgeResult.Failure failure:
                return new PipeEnvelope { Ok = false, Error = failure.Message };

            case BridgeResult.Raw raw:
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
                var element = JsonSerializer.SerializeToElement(dto, JsonOptions);
                return new PipeEnvelope { Ok = true, Result = element };

            case BridgeResult.Payload payload:
                return new PipeEnvelope
                {
                    Ok = true,
                    Result = JsonSerializer.SerializeToElement(payload.Dto, payload.Dto.GetType(), JsonOptions),
                };

            default:
                return new PipeEnvelope { Ok = false, Error = "未知结果。" };
        }
    }

    private static string Serialize(PipeEnvelope envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);

    public void Dispose() => _cts.Cancel();
}
