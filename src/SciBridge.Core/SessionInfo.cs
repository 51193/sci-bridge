using System.Text.Json;
using System.Text.Json.Serialization;

namespace SciBridge;

/// <summary>
/// The short-lived descriptor of a live bridge. It never contains the SSH password.
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
    public string Pipe { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ExePath { get; set; } = "";
    public bool AllowSymlinks { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SessionInfo? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(path), Options);
            if (info is null || string.IsNullOrWhiteSpace(info.Token)) return null;
            return info;
        }
        catch
        {
            return null;
        }
    }

    public void SaveAtomic(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Deletes the descriptor only when it still belongs to this session.</summary>
    public void DeleteIfOwned(string path)
    {
        try
        {
            var existing = TryLoad(path);
            if (existing is not null && existing.Token == Token) File.Delete(path);
        }
        catch
        {
            // A leftover descriptor is harmless: the CLI validates liveness before use.
        }
    }
}
