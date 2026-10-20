using System.Text.Json.Serialization;

namespace SciBridge;

/// <summary>One directory entry. Deliberately narrow: no owner names, no capabilities.</summary>
public sealed class EntryDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>One of: dir, file, link, other.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "other";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>UTC modification time, ISO-8601.</summary>
    [JsonPropertyName("mtime")] public string Mtime { get; set; } = "";
    /// <summary>Permission bits for owner/group/other, e.g. "rwxr-x---".</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("symlink")] public bool Symlink { get; set; }
}

public sealed class ListDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("entries")] public List<EntryDto> Entries { get; set; } = new();
}

public sealed class StatDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
    [JsonPropertyName("entry")] public EntryDto Entry { get; set; } = new();
}

public sealed class ReadDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("offset")] public long Offset { get; set; }
    /// <summary>Total size of the remote file in bytes.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("bytes")] public int Bytes { get; set; }
    [JsonPropertyName("eof")] public bool Eof { get; set; }
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "base64";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

public sealed class FindDto
{
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
    [JsonPropertyName("glob")] public string Glob { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("dirsVisited")] public int DirsVisited { get; set; }
    [JsonPropertyName("entries")] public List<EntryDto> Entries { get; set; } = new();
}

public sealed class HealthDto
{
    [JsonPropertyName("service")] public string Service { get; set; } = "scibridge";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = ".";
    [JsonPropertyName("home")] public string Home { get; set; } = "";
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; } = true;
    [JsonPropertyName("allowSymlinks")] public bool AllowSymlinks { get; set; }
    [JsonPropertyName("operations")] public List<string> Operations { get; set; } = new();
    [JsonPropertyName("transport")] public string Transport { get; set; } = "";
}

/// <summary>Envelope used by the named-pipe transport, which is always JSON.</summary>
public sealed class PipeEnvelope
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; }
    [JsonPropertyName("glob")] public string? Glob { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
    [JsonPropertyName("depth")] public int Depth { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }

    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("result")] public System.Text.Json.JsonElement? Result { get; set; }
}
