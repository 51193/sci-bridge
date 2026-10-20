using System.Security.Cryptography;
using System.Text;

namespace SciBridge.App;

/// <summary>
/// Optional "remember password" support. The password is encrypted with Windows
/// DPAPI scoped to the current user, so another account on the same machine cannot
/// read it. It is never written anywhere in plaintext.
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SciBridge.session.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // Wrong user profile, corrupted blob, or a moved settings file.
            Log.Write("password unprotect failed: " + ex.GetType().Name);
            return null;
        }
    }
}
