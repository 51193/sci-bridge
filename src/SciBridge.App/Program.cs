using System.Runtime.InteropServices;

namespace SciBridge.App;

internal static class Program
{
    private const string MutexName = @"Local\SciBridge.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Hidden helper mode: the staged new exe waits for this process to exit,
        // replaces the installed exe, and starts it again.
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            ReleaseConsoleWindow();
            return UpdateApplier.Run(args);
        }

        // Diagnostics: report the real laid-out geometry so the UI can be verified
        // without a human looking at it (used by the automated checks).
        if (args.Length > 0 && args[0] == "--dump-ui")
        {
            ApplicationConfiguration.Initialize();
            return UiDump.Run();
        }

        // Headless end-to-end check against a real server, without opening a window.
        if (args.Length > 0 && args[0] == "--self-test")
        {
            return SelfTest.Run(args);
        }

        if (args.Length > 0)
        {
            return RunDiagnostic(args);
        }

        // No arguments: the user double-clicked the executable.
        ReleaseConsoleWindow();
        UpdateApplier.CleanupBackupOnStartup();
        UpdateService.CleanupOldUpdates();
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "SciBridge 已经在运行了。\n\n请使用已经打开的那个窗口；" +
                "如果要重新连接，请先在原窗口点击“断开”。",
                "SciBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Only human diagnostics remain on the command line. Agents use the loopback
    /// HTTP endpoint from the handoff text, never this executable.
    /// </summary>
    private static int RunDiagnostic(string[] args)
    {
        switch (args[0])
        {
            case "--version" or "-v" or "version":
                Console.WriteLine("SciBridge " + BuildInfo.Version);
                return 0;

            case "--help" or "-h" or "/?" or "help":
                PrintHelp();
                return 0;

            // Old handoffs / old skills may still try the removed CLI verbs.
            case "health" or "ls" or "cat" or "stat" or "find":
                Console.Error.WriteLine("agent CLI 已移除：Codex 不再执行 SciBridge.exe。");
                Console.Error.WriteLine("请使用交接文字里的本机 HTTP 地址（curl.exe 访问 127.0.0.1）。");
                return 2;

            default:
                Console.Error.WriteLine($"未知参数：{args[0]}");
                Console.Error.WriteLine("Codex 请使用交接文字里的 HTTP 地址，不要调用 SciBridge.exe。");
                PrintHelp();
                return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            SciBridge {BuildInfo.Version} — 机构服务器只读数据桥

            用法：
              双击 SciBridge.exe                 打开窗口，连接服务器
              SciBridge.exe --self-test [选项]   不打开窗口，连接服务器并跑一遍只读操作
              SciBridge.exe --dump-ui            导出窗口控件几何（自动验证用）
              SciBridge.exe --version

            连接成功后，窗口里的“交接文字”包含本机 HTTP 地址和短期令牌。
            把整段文字粘给 Codex，Codex 用 curl.exe 访问 127.0.0.1 即可读取数据；
            不需要执行 SciBridge.exe，也不需要读取任何会话文件。

            只读保证：只提供列目录、看属性、读文件、按名字查找。
            没有任何写入、删除、改名、上传或远程命令执行，且被限制在“可读根目录”内。

            --self-test 选项：
              --host <地址>    默认 127.0.0.1
              --port <端口>    默认 2222
              --user <账号>    默认 test
              --root <目录>    可读根目录，默认 .
              --password <密码>  不填则交互式输入（推荐）
              --allow-symlinks 允许跟随符号链接（一般不要用）
            """);
    }

    /// <summary>
    /// The executable is a console-subsystem binary so diagnostics keep working
    /// from cmd/PowerShell. In GUI mode, detach from the console: if the console
    /// was created just for this process (double-click), it closes; if it is the
    /// user's existing cmd/PowerShell console, it is left untouched.
    /// </summary>
    private static void ReleaseConsoleWindow()
    {
        try { FreeConsole(); }
        catch { /* Cosmetic only; never fail startup because of it. */ }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
