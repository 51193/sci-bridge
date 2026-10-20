using System.Text;

namespace SciBridge.App;

/// <summary>
/// A headless end-to-end check: connect, start the bridge, and exercise every
/// read-only operation through the same client path an agent uses.
///
/// This exists so a user can answer "does this actually work on my machine?"
/// with one command, without needing Codex, and without the friend's server.
/// It also lets the automated checks drive the real Windows executable.
/// </summary>
internal static class SelfTest
{
    private const int SampleBytes = 512;

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console */ }

        var options = Parse(args);
        if (options is null) return 2;

        Console.WriteLine();
        Console.WriteLine("SciBridge 自检");
        Console.WriteLine("=========================================================");
        Console.WriteLine($"  目标服务器 : {options.Host}:{options.Port}");
        Console.WriteLine($"  账号       : {options.User}");
        Console.WriteLine($"  可读根目录 : {options.Root}");
        Console.WriteLine();

        var password = options.Password ?? ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("没有输入密码，自检结束。");
            return 2;
        }

        var results = new List<(string Name, bool Ok, string Detail)>();
        void Check(string name, bool ok, string detail = "") => results.Add((name, ok, detail));

        RemoteFs? fileSystem = null;
        BridgeHost? bridge = null;

        try
        {
            Console.WriteLine("正在连接（第一次连接需要确认服务器指纹）…");
            fileSystem = SshConnector.Connect(
                new ConnectRequest(options.Host, options.Port, options.User, password,
                    options.Root, options.AllowSymlinks),
                new HostKeyStore(),
                new ConsoleInteraction());

            bridge = BridgeHost.Start(fileSystem, options.Host, options.Port, options.User);
            Console.WriteLine($"桥已启动：{bridge.Session.Url}（仅本机）");
            Console.WriteLine();

            using var client = BridgeClient.Create(bridge.SessionPath);

            // 1) health
            var health = client.Call(new BridgeRequest("health")) as HealthDto;
            Check("health 返回服务信息",
                health is { ReadOnly: true } && health.Operations.Count >= 4,
                health is null ? "没有返回" : $"root={health.Root} home={health.Home}");

            // 2) root listing
            var root = client.Call(new BridgeRequest("list", ".")) as ListDto;
            Check("列出可读根目录",
                root is { Count: > 0 },
                root is null ? "没有返回" : $"共 {root.Count} 项，前几项：" +
                    string.Join(", ", root.Entries.Take(5).Select(e => e.Name)));

            // 3) stat
            var stat = client.Call(new BridgeRequest("stat", ".")) as StatDto;
            Check("查看路径属性", stat is not null, stat is null ? "没有返回" : stat.Entry.Type);

            // 4) read a real file, found by walking down a few levels
            var (filePath, fileSize) = FindSampleFile(client, root);
            if (filePath is null)
            {
                Check("读取一个文件", true, "跳过：可读范围内没有找到非空文件");
            }
            else
            {
                var read = client.Call(new BridgeRequest("read", filePath, 0, SampleBytes)) as ReadDto;
                var got = read is null ? 0 : Convert.FromBase64String(read.Data).Length;
                Check($"读取文件 {filePath}（{fileSize} 字节）",
                    read is not null && got > 0,
                    read is null ? "没有返回" : $"取回 {got} 字节");
            }

            // 5) find
            var found = client.Call(new BridgeRequest("find", ".", Glob: "*.txt", Max: 50, Depth: 3)) as FindDto;
            Check("按文件名查找", found is not null,
                found is null ? "没有返回" : $"匹配 {found.Count} 项，扫描 {found.DirsVisited} 个目录");

            // 6) confinement: ".." must be refused
            var escapeRefused = false;
            var escapeMessage = "";
            try
            {
                client.Call(new BridgeRequest("list", ".."));
            }
            catch (BridgeException ex)
            {
                escapeRefused = true;
                escapeMessage = ex.Message;
            }
            Check("拒绝越出可读根目录的路径", escapeRefused, escapeMessage);

            // 7) the read-only surface: there is no write operation at all
            var writeRefused = false;
            var writeMessage = "";
            try
            {
                client.Call(new BridgeRequest("write", "should-not-exist.txt"));
            }
            catch (BridgeException ex)
            {
                writeRefused = true;
                writeMessage = ex.Message;
            }
            Check("不存在任何写入操作", writeRefused, writeMessage);
        }
        catch (BridgeException ex)
        {
            Check("建立连接", false, ex.Message);
        }
        catch (Exception ex)
        {
            Check("建立连接", false, ex.ToString());
        }
        finally
        {
            bridge?.Dispose();
            fileSystem?.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("---------------------------------------------------------");
        var failed = results.Count(r => !r.Ok);
        foreach (var (name, ok, detail) in results)
        {
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name}" +
                              (string.IsNullOrWhiteSpace(detail) ? "" : $"    [{detail}]"));
        }
        Console.WriteLine("---------------------------------------------------------");

        if (failed == 0)
        {
            Console.WriteLine($"  自检结果：全部通过（{results.Count} 项）。");
            Console.WriteLine();
            Console.WriteLine("  下一步：双击 SciBridge.exe，填入同样的服务器信息并连接，");
            Console.WriteLine("  把窗口里的交接文字粘给 Codex，就可以让 Codex 读数据了。");
        }
        else
        {
            Console.WriteLine($"  自检结果：{failed} 项失败，共 {results.Count} 项。");
            Console.WriteLine("  请把上面的内容发给帮你准备这个工具的人（不需要包含密码）。");
        }
        Console.WriteLine();

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Finds one small readable file so the read path is genuinely exercised.</summary>
    private static (string? Path, long Size) FindSampleFile(BridgeClient client, ListDto? root)
    {
        if (root is null) return (null, 0);

        foreach (var entry in root.Entries)
        {
            if (entry.Type != "file" || entry.Size <= 0) continue;
            return (entry.Name, entry.Size);
        }

        foreach (var directory in root.Entries.Where(e => e.Type == "dir").Take(5))
        {
            ListDto? children;
            try { children = client.Call(new BridgeRequest("list", directory.Name, Max: 200)) as ListDto; }
            catch (BridgeException) { continue; }
            if (children is null) continue;

            foreach (var entry in children.Entries)
            {
                if (entry.Type == "file" && entry.Size > 0)
                    return ($"{directory.Name}/{entry.Name}", entry.Size);
            }
        }

        return (null, 0);
    }

    private sealed record Options(
        string Host, int Port, string User, string Root, string? Password, bool AllowSymlinks);

    private static Options? Parse(string[] args)
    {
        var host = "127.0.0.1";
        var port = 2222;
        var user = "test";
        var root = ".";
        string? password = null;
        var allowSymlinks = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": if (!Next(args, ref i, out host)) return null; break;
                case "--port": if (!Next(args, ref i, out var p) || !int.TryParse(p, out port)) return null; break;
                case "--user": if (!Next(args, ref i, out user)) return null; break;
                case "--root": if (!Next(args, ref i, out root)) return null; break;
                case "--password": if (!Next(args, ref i, out password)) return null; break;
                case "--allow-symlinks": allowSymlinks = true; break;
                case "--help" or "-h":
                    Console.WriteLine("""
                        SciBridge.exe --self-test [选项]

                        不打开窗口，直接连接服务器并把每个只读操作跑一遍，用来确认工具
                        在这台电脑上是否能正常工作。

                          --host <地址>    默认 127.0.0.1
                          --port <端口>    默认 2222
                          --user <账号>    默认 test
                          --root <目录>    可读根目录，默认 .
                          --password <密码>  不填则交互式输入（推荐）
                          --allow-symlinks 允许跟随符号链接（一般不要用）
                        """);
                    return null;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    return null;
            }
        }

        return new Options(host, port, user, root, password, allowSymlinks);
    }

    private static bool Next(string[] args, ref int i, out string value)
    {
        value = "";
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"参数 {args[i]} 后面缺少值。");
            return false;
        }
        value = args[++i];
        return true;
    }

    /// <summary>Reads the SSH password without echoing it.</summary>
    private static string ReadPassword()
    {
        Console.Write("请输入 SSH 密码（输入时不显示）：");

        if (Console.IsInputRedirected)
        {
            var line = Console.In.ReadLine() ?? "";
            Console.WriteLine();
            return line.TrimEnd('\r', '\n');
        }

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }

    /// <summary>Console version of the host-key prompt, so self-test also covers trust-on-first-use.</summary>
    private sealed class ConsoleInteraction : IUserInteraction
    {
        public HostKeyDecision ConfirmHostKey(HostKeyCheck check)
        {
            var changed = check.Verdict == HostKeyVerdict.Changed;
            Console.WriteLine();
            Console.WriteLine(changed
                ? "警告：这台服务器的主机公钥和上次记录的不一样！"
                : "第一次连接这台服务器，请确认它的身份：");
            Console.WriteLine($"  服务器   : {check.Host}:{check.Port}");
            Console.WriteLine($"  公钥类型 : {check.KeyType}");
            if (changed) Console.WriteLine($"  已记录   : {check.PinnedFingerprintSha256}");
            Console.WriteLine($"  本次指纹 : {check.FingerprintSha256}");
            Console.WriteLine();

            if (changed)
            {
                Console.Write("只有在你已向管理员核实过的情况下才继续。替换为新指纹？[y/N] ");
                return Ask() ? HostKeyDecision.AcceptAndPin : HostKeyDecision.Reject;
            }

            Console.Write("接受并记住这个指纹？[Y/n] ");
            return Ask(defaultYes: true) ? HostKeyDecision.AcceptAndPin : HostKeyDecision.Reject;
        }

        public string? AnswerChallenge(string instruction, string request, bool echoed)
        {
            Console.WriteLine();
            if (!string.IsNullOrWhiteSpace(instruction)) Console.WriteLine(instruction);
            Console.Write(string.IsNullOrWhiteSpace(request) ? "请输入：" : request + " ");
            var answer = Console.IsInputRedirected ? Console.In.ReadLine() : Console.ReadLine();
            return answer;
        }

        private static bool Ask(bool defaultYes = false)
        {
            var line = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (line.Length == 0) return defaultYes;
            return line is "y" or "yes" or "是";
        }
    }
}
