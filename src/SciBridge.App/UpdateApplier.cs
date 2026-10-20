using System.Diagnostics;

namespace SciBridge.App;

/// <summary>
/// Hidden helper mode: waits for the old GUI process to exit, backs up the
/// installed exe, replaces it with the downloaded exe, and starts the new one.
/// It runs from the staged new exe, so the old exe is never locked by itself.
/// </summary>
internal static class UpdateApplier
{
    public static int Run(string[] args)
    {
        var parentPidText = Value(args, "--parent-pid");
        var installExeArg = Value(args, "--install-exe");
        var stagedExeArg = Value(args, "--staged-exe");

        if (!int.TryParse(parentPidText, out var parentPid) || parentPid <= 0 ||
            string.IsNullOrWhiteSpace(installExeArg) ||
            string.IsNullOrWhiteSpace(stagedExeArg))
        {
            return Fail("更新参数不完整。");
        }

        try
        {
            var installExe = Path.GetFullPath(installExeArg);
            var stagedExe = Path.GetFullPath(stagedExeArg);
            var updatesRoot = Path.GetFullPath(Path.Combine(AppPaths.DataDir, "updates"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!stagedExe.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
                return Fail("暂存文件不在更新目录内。");
            if (!File.Exists(installExe)) return Fail("找不到当前安装的 SciBridge.exe。");
            if (!File.Exists(stagedExe)) return Fail("找不到已下载的新版本。");

            WaitForParent(parentPid);
            var backup = installExe + ".bak";
            try
            {
                Replace(installExe, stagedExe);
                StartNewVersion(installExe);
            }
            catch
            {
                // If replacement already happened but the new exe failed to
                // start, put the old exe back before surfacing the error.
                try
                {
                    if (File.Exists(backup)) File.Copy(backup, installExe, overwrite: true);
                }
                catch { /* keep the backup for manual recovery */ }
                throw;
            }

            Log.Write($"update applied: {installExe}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("update apply failed: " + ex);
            return Fail("更新未完成。旧版本通常已保留；如果程序无法启动，请检查同目录下的 .bak 文件。\n\n" + ex.Message);
        }
    }

    private static void WaitForParent(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            if (!parent.WaitForExit(60_000))
                throw new TimeoutException("等待旧进程退出超时。");
        }
        catch (ArgumentException)
        {
            // The old process is already gone.
        }
    }

    private static void Replace(string installExe, string stagedExe)
    {
        var temp = installExe + ".new";
        var backup = installExe + ".bak";

        File.Copy(stagedExe, temp, overwrite: true);

        try
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Replace(temp, installExe, backup, ignoreMetadataErrors: true);
        }
        catch
        {
            // Some filesystems do not support File.Replace; use rename+rollback.
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(installExe, backup);
            try
            {
                File.Move(temp, installExe);
            }
            catch
            {
                File.Move(backup, installExe);
                throw;
            }
        }
    }

    private static void StartNewVersion(string installExe)
    {
        var startInfo = new ProcessStartInfo(installExe)
        {
            // The app is a console-subsystem binary; CREATE_NO_WINDOW avoids
            // even a brief console/pwsh window when the helper restarts it.
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installExe) ?? "",
        };

        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("无法启动新版本。");
    }

    /// <summary>
    /// Called on the next GUI start: the new exe is running, so the old backup
    /// is no longer needed.
    /// </summary>
    public static void CleanupBackupOnStartup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var backup = exe + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
            // Best effort; a leftover .bak never affects the app.
        }
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int Fail(string message)
    {
        Log.Write("update apply: " + message.Replace('\n', ' '));
        try
        {
            MessageBox.Show(message, "SciBridge 更新失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // If there is no interactive session, the log is the only witness.
        }
        return 1;
    }
}
