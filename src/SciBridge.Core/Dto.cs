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
    [JsonPropertyName("rootReachable")] public bool RootReachable { get; set; }
    [JsonPropertyName("allowSymlinks")] public bool AllowSymlinks { get; set; }
    [JsonPropertyName("allowUpload")] public bool AllowUpload { get; set; }
    [JsonPropertyName("allowMkdir")] public bool AllowMkdir { get; set; }
    [JsonPropertyName("operations")] public List<string> Operations { get; set; } = new();
    [JsonPropertyName("writeOperations")] public List<string> WriteOperations { get; set; } = new();
    [JsonPropertyName("transport")] public string Transport { get; set; } = "";
}

public sealed class WriteDto
{
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
}
