using System.Reflection;

namespace SciBridge;

/// <summary>
/// The version embedded into the built assembly by MSBuild. There is no version
/// literal in C#: the Release workflow passes -p:Version=&lt;tag without v&gt;,
/// while local builds use the 0.0.0-dev placeholder from the project file.
/// </summary>
public static class BuildInfo
{
    public const string ServiceName = "scibridge";

    public static string Version { get; } = ResolveVersion();

    public static string DisplayVersion => "SciBridge " + Version;

    /// <summary>Repository URL embedded by MSBuild (AssemblyMetadata).</summary>
    public static string RepositoryUrl { get; } = ResolveMetadata("RepositoryUrl");

    private static string ResolveVersion()
    {
        var assembly = EntryAssembly;
        var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var informational = attribute?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // A CI build may append "+<commit>"; the version authority is still the tag.
            var plus = informational.IndexOf('+');
            if (plus >= 0) informational = informational[..plus];
            if (!string.IsNullOrWhiteSpace(informational)) return informational;
        }

        var numeric = assembly.GetName().Version;
        return numeric is null ? "dev" : numeric.ToString(3);
    }

    private static string ResolveMetadata(string key)
    {
        var attribute = EntryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
        return attribute?.Value ?? "";
    }

    private static Assembly EntryAssembly =>
        Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
}
