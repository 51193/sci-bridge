namespace SciBridge;

/// <summary>Stable release versions only (major.minor.patch, optional leading v).</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        // Pre-release and build metadata are not stable release versions.
        if (text.Contains('-') || text.Contains('+')) return false;

        var parts = text.Split('.');
        if (parts.Length != 3) return false;

        if (!TryPart(parts[0], out var major) ||
            !TryPart(parts[1], out var minor) ||
            !TryPart(parts[2], out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    private static bool TryPart(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 9) return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') return false;
        }
        return int.TryParse(text, out value);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;
        var byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
