using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SciBridge;

public sealed record ConnectRequest(
    string Host,
    int Port,
    string User,
    string Password,
    string Root,
    bool AllowSymlinks,
    bool AllowUpload = false,
    bool AllowMkdir = false,
    int TimeoutSeconds = 20);

public enum HostKeyDecision
{
    /// <summary>Pin the fingerprint and continue.</summary>
    AcceptAndPin,
    UseOnce,
    Reject,
}

/// <summary>Callbacks the connector needs from whatever is driving it (GUI or console).</summary>
public interface IUserInteraction
{
    HostKeyDecision ConfirmHostKey(HostKeyCheck check);

    /// <summary>
    /// Answers a keyboard-interactive prompt (2FA codes, password change, etc.).
    /// Return null to decline.
    /// </summary>
    string? AnswerChallenge(string instruction, string request, bool echoed);
}

/// <summary>Non-interactive fallback: refuses anything that needs a human.</summary>
public sealed class RefuseInteraction : IUserInteraction
{
    public HostKeyDecision ConfirmHostKey(HostKeyCheck check) => HostKeyDecision.Reject;
    public string? AnswerChallenge(string instruction, string request, bool echoed) => null;
}

public static class SshConnector
{
    /// <summary>
    /// Opens exactly one authenticated SFTP session and wraps it in the read-only
    /// view. Authentication is attempted once only, so a wrong password cannot
    /// walk an institutional account into a lockout.
    /// </summary>
    public static RemoteFs Connect(ConnectRequest request, HostKeyStore store, IUserInteraction interaction)
    {
        if (string.IsNullOrWhiteSpace(request.Host)) throw new BridgeException("请填写服务器地址。");
        if (string.IsNullOrWhiteSpace(request.User)) throw new BridgeException("请填写账号。");
        if (request.Port is < 1 or > 65535) throw new BridgeException("端口必须在 1-65535 之间。");

        var normalizedRoot = PathGuard.NormalizeRoot(request.Root);

        using var passwordAuth = new PasswordAuthenticationMethod(request.User, request.Password ?? "");
        using var keyboardAuth = new KeyboardInteractiveAuthenticationMethod(request.User);

        keyboardAuth.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                string? answer;
                if (!prompt.IsEchoed && IsPasswordPrompt(prompt.Request))
                {
                    // The server asked for the account password again: answer it without a second dialog.
                    answer = request.Password;
                }
                else
                {
                    answer = interaction.AnswerChallenge(e.Instruction ?? "", prompt.Request ?? "", prompt.IsEchoed);
                }

                if (answer is null)
                    throw new SshAuthenticationException("用户取消了对服务器提问的回答。");
                prompt.Response = answer;
            }
        };

        var connectionInfo = new ConnectionInfo(request.Host, request.Port, request.User,
            keyboardAuth, passwordAuth)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 120)),
            // One attempt only: repeated attempts risk locking an institutional account.
            RetryAttempts = 1,
            ChannelCloseTimeout = TimeSpan.FromSeconds(10),
        };

        var client = new SftpClient(connectionInfo)
        {
            OperationTimeout = TimeSpan.FromSeconds(45),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = 64 * 1024,
        };

        var rejectedFingerprint = false;
        var rejectionReason = "";

        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = NormalizeFingerprint(e.FingerPrintSHA256, e.HostKey);
            var keyType = string.IsNullOrWhiteSpace(e.HostKeyName) ? "unknown" : e.HostKeyName;

            var check = store.Check(request.Host, request.Port, keyType, fingerprint);
            Log.Write($"host key {request.Host}:{request.Port} {keyType} {fingerprint} -> {check.Verdict}");

            if (check.Verdict == HostKeyVerdict.Trusted)
            {
                e.CanTrust = true;
                return;
            }

            var decision = interaction.ConfirmHostKey(check);
            switch (decision)
            {
                case HostKeyDecision.AcceptAndPin:
                    store.Pin(request.Host, request.Port, keyType, fingerprint);
                    e.CanTrust = true;
                    break;
                case HostKeyDecision.UseOnce:
                    e.CanTrust = true;
                    break;
                default:
                    e.CanTrust = false;
                    rejectedFingerprint = true;
                    rejectionReason = check.Verdict == HostKeyVerdict.Changed
                        ? $"服务器主机公钥已变化（{keyType}）。已保存的指纹：{check.PinnedFingerprintSha256}，" +
                          $"本次收到：{fingerprint}。已停止连接。请联系机构管理员通过可信渠道核实。"
                        : $"已拒绝未知的服务器主机公钥（{keyType}，指纹 {fingerprint}）。";
                    break;
            }
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (rejectedFingerprint) throw new BridgeException(rejectionReason, ex);
            throw new BridgeException(Describe(ex, request), ex);
        }

        RemoteFs fs;
        try
        {
            fs = new RemoteFs(client, normalizedRoot, request.AllowSymlinks);
            // Fail fast when the access root is wrong, instead of confusing the agent later.
            fs.Stat(".");
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new BridgeException(
                $"已登录，但可读根目录 '{normalizedRoot}' 无法访问：{ex.Message}", ex);
        }

        Log.Write($"connected {request.User}@{request.Host}:{request.Port} root={normalizedRoot} home={fs.Home}");
        return fs;
    }

    private static bool IsPasswordPrompt(string? request)
    {
        if (string.IsNullOrEmpty(request)) return false;
        var r = request.ToLowerInvariant();
        return r.Contains("password") || r.Contains("密码") || r.Contains("passphrase");
    }

    /// <summary>
    /// Produces the canonical OpenSSH fingerprint form, "SHA256:base64" without
    /// padding. SSH.NET reports the bare base64, which would not match what an
    /// administrator or ssh-keyscan prints, so the prefix is added here. Every
    /// comparison and stored value uses this canonical form.
    /// </summary>
    public static string NormalizeFingerprint(string? reported, byte[]? hostKey)
    {
        var text = reported?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return ComputeFingerprint(hostKey);

        const string prefix = "SHA256:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..];

        return prefix + text.TrimEnd('=');
    }

    internal static string ComputeFingerprint(byte[]? hostKey)
    {
        if (hostKey is null || hostKey.Length == 0) return "(unavailable)";
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
    }

    /// <summary>Turns transport and authentication failures into something a researcher can act on.</summary>
    public static string Describe(Exception ex, ConnectRequest request)
    {
        return ex switch
        {
            SshAuthenticationException =>
                $"认证失败：账号 '{request.User}' 的用户名或密码不正确，或服务器不接受密码登录。" +
                "密码只在本机窗口输入，请核对后重试；连续失败可能导致账号被锁定，请不要反复尝试。",
            SshOperationTimeoutException or TimeoutException =>
                $"连接 {request.Host}:{request.Port} 超时。请确认电脑已接入机构网络或 VPN，且服务器 SSH 端口可达。",
            SocketException se =>
                $"无法连接到 {request.Host}:{request.Port}（{se.SocketErrorCode}）。请确认地址、端口，以及是否需要先连接 VPN。",
            SshConnectionException sce =>
                $"与 {request.Host}:{request.Port} 的连接中断：{sce.Message}。若服务器要求特定加密算法或跳板机，请联系机构管理员。",
            SshException se2 => $"SSH 协议错误：{se2.Message}",
            _ => ex.Message,
        };
    }
}
