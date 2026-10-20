using System.Text.Json;
using System.Text.Json.Serialization;

namespace SciBridge;

/// <summary>
/// Non-secret user preferences plus an opaque DPAPI-protected password blob.
/// The Core never sees the plaintext password on disk; the Windows GUI owns
/// the encryption and decryption of <see cref="PasswordBlob"/>.
/// </summary>
public sealed class Settings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Root { get; set; } = ".";
    public bool AllowSymlinks { get; set; }
    public bool RememberPassword { get; set; }

    /// <summary>Base64 DPAPI blob, or empty. Never a plaintext password.</summary>
    public string PasswordBlob { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load(string? path = null)
    {
        try
        {
            var file = path ?? AppPaths.SettingsFile;
            if (!File.Exists(file)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Options) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            var file = path ?? AppPaths.SettingsFile;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }
}
