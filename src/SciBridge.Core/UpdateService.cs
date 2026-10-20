using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace SciBridge;

public sealed record UpdateInfo(
    ReleaseVersion Version,
    string TagName,
    string ExeAssetName,
    string ExeDownloadUrl,
    long ExeSize,
    string ChecksumsDownloadUrl,
    string ReleasePageUrl);

/// <summary>
/// Checks the latest stable GitHub release and downloads its standalone exe.
/// The updater never touches docs, skills or the test server; only SciBridge.exe.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(10));

    public static async Task<UpdateInfo?> CheckAsync(string localVersion, CancellationToken ct)
    {
        // Development builds (0.0.0-dev) and non-stable local versions do not
        // auto-update; a release build always carries a stable version.
        if (!ReleaseVersion.TryParse(localVersion, out var local)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateConfig.LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("SciBridge-Updater");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await ApiClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"update check: GitHub returned {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            if (!ReleaseVersion.TryParse(tag, out var remote)) return null;
            if (remote.CompareTo(local) <= 0) return null;

            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? exeName = null;
            string? exeUrl = null;
            string? checksumsUrl = null;
            long exeSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? ""
                    : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString() ?? ""
                    : "";
                if (name.Length == 0 || url.Length == 0) continue;

                if (name.Equals(UpdateConfig.ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = url;
                }
                else if (name.EndsWith(UpdateConfig.ExeAssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // Ambiguous assets are a release mistake; refuse to guess.
                    if (exeUrl is not null) return null;
                    exeName = name;
                    exeUrl = url;
                    if (asset.TryGetProperty("size", out var sizeElement) &&
                        sizeElement.TryGetInt64(out var size))
                    {
                        exeSize = size;
                    }
                }
            }

            if (exeUrl is null || exeName is null || checksumsUrl is null)
            {
                Log.Write("update check: release is missing the exe or SHA256SUMS.txt");
                return null;
            }
            if (!IsTrustedReleaseUrl(exeUrl) || !IsTrustedReleaseUrl(checksumsUrl))
            {
                Log.Write("update check: release asset URL is not hosted by GitHub");
                return null;
            }

            var pageUrl = root.TryGetProperty("html_url", out var pageElement)
                ? pageElement.GetString() ?? ""
                : "";

            return new UpdateInfo(remote, tag, exeName, exeUrl, exeSize, checksumsUrl, pageUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the checksums and exe into %LOCALAPPDATA%\SciBridge\updates\&lt;version&gt;,
    /// verifies SHA-256, and returns the staged exe path.
    /// </summary>
    public static async Task<string> DownloadAndVerifyAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var versionDir = Path.Combine(AppPaths.DataDir, "updates", info.Version.ToString());
        Directory.CreateDirectory(versionDir);

        var checksumsPath = Path.Combine(versionDir, UpdateConfig.ChecksumsAssetName);
        await DownloadFileAsync(
            info.ChecksumsDownloadUrl, checksumsPath, UpdateConfig.MaxChecksumsBytes,
            expectedBytes: 0, progress: null, ct).ConfigureAwait(false);

        var expectedHash = await FindExpectedHashAsync(checksumsPath, info.ExeAssetName, ct)
            .ConfigureAwait(false);
        if (expectedHash is null)
        {
            throw new InvalidOperationException(
                $"发布包的 {UpdateConfig.ChecksumsAssetName} 中没有 {info.ExeAssetName} 的校验值。");
        }

        var target = Path.Combine(versionDir, "SciBridge.exe");
        var temp = target + ".download";
        TryDelete(temp);

        try
        {
            await DownloadFileAsync(
                info.ExeDownloadUrl, temp, UpdateConfig.MaxExeBytes,
                info.ExeSize, progress, ct).ConfigureAwait(false);

            var actualHash = await Sha256Async(temp, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("下载的 exe SHA-256 与发布包不一致。");
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task DownloadFileAsync(
        string url, string path, long maxBytes, long expectedBytes,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SciBridge-Updater");

        using var response = await DownloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maxBytes)
            throw new InvalidOperationException("下载内容超过大小上限。");

        TryDelete(path);
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException("下载内容超过大小上限。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                var denominator = contentLength is > 0 ? contentLength.Value : expectedBytes;
                if (progress is not null && denominator > 0)
                    progress.Report(Math.Min(1.0, (double)total / denominator));
            }
        }
    }

    private static async Task<string?> FindExpectedHashAsync(
        string checksumsPath, string assetName, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(checksumsPath, ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var name = parts[^1].TrimStart('*');
            if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0].Trim().ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsTrustedReleaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>Best-effort cleanup for old staging directories.</summary>
    public static void CleanupOldUpdates()
    {
        try
        {
            var root = Path.Combine(AppPaths.DataDir, "updates");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromDays(7))
                    continue;
                try { Directory.Delete(dir, recursive: true); }
                catch { /* in use or not ours */ }
            }
        }
        catch
        {
            // Update cleanup must never break startup.
        }
    }
}
