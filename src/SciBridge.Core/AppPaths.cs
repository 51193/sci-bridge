using System.Runtime.InteropServices;

namespace SciBridge;

/// <summary>Well-known per-user locations shared by the GUI and diagnostics.</summary>
public static class AppPaths
{
    public const string AppName = "SciBridge";

    public static string DataDir { get; } = ResolveDataDir();

    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "scibridge.log");

    private static string ResolveDataDir()
    {
        string? dir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            dir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
        }

        if (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, AppName);
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // Fall through to the executable-relative location.
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
