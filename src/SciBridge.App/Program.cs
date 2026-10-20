using System.Runtime.InteropServices;

namespace SciBridge.App;

internal static class Program
{
    private const string MutexName = @"Local\SciBridge.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostics: report the real laid-out geometry so the UI can be verified
        // without a human looking at it (used by the automated checks). Checked before
        // the CLI so it is not mistaken for an unknown command.
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

        // Any argument means "command line": run it and never open a window.
        var cliResult = CliRunner.TryRun(args);
        if (cliResult is not null) return cliResult.Value;

        // No arguments: the user double-clicked the executable.
        HideConsoleWindow();
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
    /// The executable is a console-subsystem binary, so Windows gives it a console
    /// window. In GUI mode that window is unwanted, so hide it before the form appears.
    /// </summary>
    private static void HideConsoleWindow()
    {
        try
        {
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) ShowWindow(handle, SW_HIDE);
        }
        catch
        {
            // Cosmetic only; never fail startup because of it.
        }
    }

    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
