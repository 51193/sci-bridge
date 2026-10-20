using System.Text;

namespace SciBridge;

/// <summary>
/// Renders results as compact, human- and model-readable text. Direct HTTP callers
/// get this by default so they do not have to parse JSON for simple lookups.
/// </summary>
public static class TextRenderer
{
    public static string Render(BridgeResult result)
    {
        switch (result)
        {
            case BridgeResult.Failure failure:
                return "error: " + failure.Message;

            case BridgeResult.Payload { Dto: ListDto list }:
            {
                var sb = new StringBuilder();
                foreach (var entry in list.Entries) sb.AppendLine(Line(entry));
                if (list.Truncated)
                    sb.AppendLine($"... 已截断（仅显示前 {list.Count} 项，可用 max 参数提高上限或缩小目录范围）");
                if (list.Entries.Count == 0 && !list.Truncated)
                    sb.AppendLine("(空目录)");
                return sb.ToString().TrimEnd('\n');
            }

            case BridgeResult.Payload { Dto: FindDto find }:
            {
                var sb = new StringBuilder();
                foreach (var entry in find.Entries) sb.AppendLine(EntryLine(entry));
                if (find.Truncated)
                    sb.AppendLine($"... 已截断（匹配 {find.Count} 项，扫描 {find.DirsVisited} 个目录；请缩小范围或提高 max）");
                if (find.Entries.Count == 0)
                    sb.AppendLine("(没有匹配项)");
                return sb.ToString().TrimEnd('\n');
            }

            case BridgeResult.Payload { Dto: StatDto stat }:
                return EntryLine(stat.Entry);

            case BridgeResult.Payload { Dto: HealthDto health }:
            {
                var sb = new StringBuilder();
                sb.AppendLine("service: " + health.Service);
                sb.AppendLine("version: " + health.Version);
                sb.AppendLine("host: " + health.Host + ":" + health.Port);
                sb.AppendLine("user: " + health.User);
                sb.AppendLine("root: " + health.Root);
                sb.AppendLine("home: " + health.Home);
                sb.AppendLine("readOnly: true");
                sb.AppendLine("rootReachable: " + (health.RootReachable ? "true" : "false"));
                sb.AppendLine("allowSymlinks: " + (health.AllowSymlinks ? "true" : "false"));
                sb.AppendLine("operations: " + string.Join(", ", health.Operations));
                return sb.ToString().TrimEnd('\n');
            }

            case BridgeResult.Raw raw:
            {
                var data = raw.Data;
                var sb = new StringBuilder();
                sb.AppendLine($"path: {data.Path}");
                sb.AppendLine($"offset: {data.Offset}");
                sb.AppendLine($"size: {data.Size}");
                sb.AppendLine($"bytes: {data.Data.Length}");
                sb.AppendLine($"eof: {(data.Offset + data.Data.Length >= data.Size ? "true" : "false")}");
                sb.AppendLine("encoding: base64");
                sb.AppendLine("data: " + Convert.ToBase64String(data.Data));
                return sb.ToString().TrimEnd('\n');
            }

            default:
                return "error: 无法渲染结果。";
        }
    }

    /// <summary>ls -l style line: permissions, size, UTC mtime, name (directories keep a trailing slash).</summary>
    public static string Line(EntryDto entry)
    {
        var type = entry.Symlink ? 'l' : entry.Type switch
        {
            "dir" => 'd',
            "file" => '-',
            _ => '?',
        };
        var name = entry.Name;
        // ls -F style markers: '/' for directories, '@' for symbolic links.
        if (entry.Symlink) name += "@";
        else if (entry.Type == "dir") name += "/";
        return $"{type}{entry.Mode} {entry.Size,12} {entry.Mtime} {name}";
    }

    private static string EntryLine(EntryDto entry) => Line(entry);
}
