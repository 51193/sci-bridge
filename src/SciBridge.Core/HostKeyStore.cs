using System.Text.Json;
using System.Text.Json.Serialization;

namespace SciBridge;

public enum HostKeyVerdict
{
    /// <summary>Fingerprint matches the pinned value.</summary>
    Trusted,
    /// <summary>Host/port/key type not seen before; the user must confirm the fingerprint.</summary>
    Unknown,
    /// <summary>A different key is already pinned for this host/port/key type.</summary>
    Changed,
}

public sealed record HostKeyCheck(
    HostKeyVerdict Verdict,
    string Host,
    int Port,
    string KeyType,
    string FingerprintSha256,
    string? PinnedFingerprintSha256);

public sealed class HostKeyEntry
{
    public string KeyType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string AddedUtc { get; set; } = "";
}

public sealed class HostKeyRecord
{
    public List<HostKeyEntry> Keys { get; set; } = new();
}

/// <summary>
/// Trust-on-first-use store for SSH host keys, keyed by host:port and key type.
/// A changed fingerprint is reported to the caller and never auto-accepted.
/// </summary>
public sealed class HostKeyStore
{
    private readonly string _path;
    private Dictionary<string, HostKeyRecord> _hosts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, HostKeyRecord> Hosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public HostKeyStore(string? path = null)
    {
        _path = path ?? AppPaths.KnownHostsFile;
        Load();
    }

    public string Path => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Options);
            if (doc?.Hosts is not null)
            {
                _hosts = new Dictionary<string, HostKeyRecord>(doc.Hosts, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A damaged store is treated as empty rather than blocking the user.
            _hosts = new Dictionary<string, HostKeyRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var doc = new Document { Hosts = _hosts };
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private static string Id(string host, int port) => host + ":" + port.ToString();

    public HostKeyCheck Check(string host, int port, string keyType, string sha256)
    {
        if (_hosts.TryGetValue(Id(host, port), out var record))
        {
            var entry = record.Keys.FirstOrDefault(k =>
                string.Equals(k.KeyType, keyType, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                // Compare canonically so an entry written before the "SHA256:" prefix
                // was normalised still matches instead of raising a false alarm.
                return string.Equals(Canonical(entry.Sha256), Canonical(sha256), StringComparison.OrdinalIgnoreCase)
                    ? new HostKeyCheck(HostKeyVerdict.Trusted, host, port, keyType, sha256, Canonical(entry.Sha256))
                    : new HostKeyCheck(HostKeyVerdict.Changed, host, port, keyType, sha256, Canonical(entry.Sha256));
            }
        }

        return new HostKeyCheck(HostKeyVerdict.Unknown, host, port, keyType, sha256, null);
    }

    /// <summary>Canonical "SHA256:base64" form, tolerant of a missing prefix or padding.</summary>
    public static string Canonical(string? fingerprint)
    {
        var text = (fingerprint ?? "").Trim();
        const string prefix = "SHA256:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..];
        return prefix + text.TrimEnd('=');
    }

    public void Pin(string host, int port, string keyType, string sha256)
    {
        var id = Id(host, port);
        if (!_hosts.TryGetValue(id, out var record))
        {
            record = new HostKeyRecord();
            _hosts[id] = record;
        }

        record.Keys.RemoveAll(k => string.Equals(k.KeyType, keyType, StringComparison.OrdinalIgnoreCase));
        record.Keys.Add(new HostKeyEntry
        {
            KeyType = keyType,
            Sha256 = Canonical(sha256),
            AddedUtc = DateTime.UtcNow.ToString("o"),
        });
        Save();
    }

    /// <summary>Replaces a changed key after the user explicitly confirms it with their administrator.</summary>
    public void Repin(string host, int port, string keyType, string sha256) => Pin(host, port, keyType, sha256);

    public IReadOnlyList<(string Host, int Port, string KeyType, string Sha256, string AddedUtc)> List()
    {
        var result = new List<(string, int, string, string, string)>();
        foreach (var (id, record) in _hosts)
        {
            var idx = id.LastIndexOf(':');
            if (idx <= 0) continue;
            var host = id[..idx];
            if (!int.TryParse(id[(idx + 1)..], out var port)) continue;
            foreach (var key in record.Keys)
            {
                result.Add((host, port, key.KeyType, key.Sha256, key.AddedUtc));
            }
        }
        return result;
    }
}
