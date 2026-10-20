namespace SciBridge;

/// <summary>
/// Confines every path an agent supplies to the configured access root.
/// Agent paths are always interpreted relative to the root; a leading '/' also
/// means "the root", never the remote filesystem root.
/// </summary>
public static class PathGuard
{
    public const int MaxPathLength = 2048;

    /// <summary>
    /// Normalises an agent-supplied path to a root-relative path using '/' separators.
    /// "." denotes the root itself. Throws <see cref="BridgeException"/> on any escape attempt.
    /// </summary>
    public static string NormalizeRelative(string? agentPath)
    {
        var raw = agentPath ?? ".";
        if (raw.Length > MaxPathLength)
            throw new BridgeException($"路径长度超过 {MaxPathLength} 个字符。");
        if (raw.Contains('\0'))
            throw new BridgeException("路径中包含非法字符。");

        // Be permissive about separators, strict about traversal.
        raw = raw.Replace('\\', '/');

        if (raw.Contains(':'))
            throw new BridgeException("路径不能包含盘符或协议前缀（例如 C: 或 http:）。");

        var parts = new List<string>();
        foreach (var segment in raw.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    throw new BridgeException("路径越出了只读可读范围，已拒绝。");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    /// <summary>
    /// Normalises the user-configured access root. Unlike agent paths this may be
    /// absolute; ".." is rejected outright because the root is a fixed anchor.
    /// </summary>
    public static string NormalizeRoot(string? root)
    {
        var raw = string.IsNullOrWhiteSpace(root) ? "." : root.Trim();
        if (raw.Length > MaxPathLength)
            throw new BridgeException("可读根目录的路径过长。");
        if (raw.Contains('\0'))
            throw new BridgeException("可读根目录包含非法字符。");

        raw = raw.Replace('\\', '/');
        var isAbsolute = raw.StartsWith('/');
        var parts = new List<string>();
        foreach (var segment in raw.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
                throw new BridgeException("可读根目录中不能包含 '..'，请改用绝对路径。");
            parts.Add(segment);
        }

        var joined = string.Join('/', parts);
        if (joined.Length == 0) return ".";
        return isAbsolute ? "/" + joined : joined;
    }

    /// <summary>Joins the root with a normalised relative path into a remote SFTP path.</summary>
    public static string Combine(string root, string relative)
    {
        if (root == "." || root.Length == 0) return relative;
        if (relative == ".") return root;
        return root.TrimEnd('/') + "/" + relative;
    }

    /// <summary>Splits a root-relative path into its non-empty components.</summary>
    public static IReadOnlyList<string> Components(string relative)
    {
        if (relative == "." || relative.Length == 0) return Array.Empty<string>();
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>True when <paramref name="child"/> is the root itself or lies beneath it.</summary>
    public static bool IsWithin(string childRelative, string parentRelative)
    {
        if (parentRelative == ".") return true;
        if (childRelative == parentRelative) return true;
        return childRelative.StartsWith(parentRelative + "/", StringComparison.Ordinal);
    }
}
