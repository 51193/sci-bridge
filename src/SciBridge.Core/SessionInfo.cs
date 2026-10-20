using System.Text.Json;

namespace SciBridge;

/// <summary>
/// The in-memory description of one live bridge. It is never written to disk:
/// agents reach the bridge over its loopback HTTP endpoint, so there is no
/// session file for a sandbox to read (or for an agent to discover).
///
/// The token is a capability limited to this machine's loopback interface, to
/// read-only operations, and to the lifetime of the bridge process.
/// </summary>
public sealed class SessionInfo
{
    public int Version { get; set; } = 1;
    public int Pid { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    /// <summary>Access root: paths handed to agents are relative to this.</summary>
    public string Root { get; set; } = ".";
    /// <summary>Remote home directory as reported by the SFTP server (display only).</summary>
    public string Home { get; set; } = "";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>Version of the running SciBridge build (embedded from the release tag).</summary>
    public string AppVersion { get; set; } = BuildInfo.Version;
    public string CreatedUtc { get; set; } = "";
    public bool AllowSymlinks { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}
