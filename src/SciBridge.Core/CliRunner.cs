using System.Text;

namespace SciBridge;

/// <summary>
/// The agent-facing command line. Every subcommand maps to a read-only operation;
/// there is no verb that could modify the remote server.
/// </summary>
public static class CliRunner
{
    /// <summary>How much of a file `cat` fetches before it starts warning about size.</summary>
    private const long DefaultCatBytes = 8L * 1024 * 1024;

    /// <summary>Returns null when the arguments mean "start the GUI" instead.</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0) return null;

        var first = args[0].Trim();
        if (first.Equals("--gui", StringComparison.OrdinalIgnoreCase)) return null;

        if (first is "--help" or "-h" or "/?" or "help") { PrintHelp(); return 0; }
        if (first is "--version" or "-v" or "version") { Console.WriteLine("SciBridge " + BuildInfo.Version); return 0; }

        return Execute(args);
    }

    private static int Execute(string[] args)
    {
        TrySetUtf8Output();

        string? sessionPath = null;
        string? outFile = null;
        string? glob = null;
        long? maxBytes = null;
        var json = false;
        var raw = false;
        var b64 = false;
        var max = 0;
        var depth = 0;
        long offset = 0;
        var length = 0;
        var positional = new List<string>();

        var command = args[0].ToLowerInvariant();
        if (command == "list") command = "ls";
        if (command == "read") command = "cat";
        if (command is not ("health" or "ls" or "stat" or "cat" or "find"))
        {
            Fail($"未知命令：{args[0]}。可用命令：health, ls, stat, cat, find（用 --help 查看说明）。");
            return 2;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--session": sessionPath = Next(args, ref i); break;
                case "--out": outFile = Next(args, ref i); break;
                case "--glob": glob = Next(args, ref i); break;
                case "--max": max = ParseInt(Next(args, ref i), max); break;
                case "--depth": depth = ParseInt(Next(args, ref i), depth); break;
                case "--offset": offset = ParseLong(Next(args, ref i), offset); break;
                case "--length": length = ParseInt(Next(args, ref i), length); break;
                case "--max-bytes": maxBytes = ParseLong(Next(args, ref i), 0); break;
                case "--json": json = true; break;
                case "--raw": raw = true; break;
                case "--b64": b64 = true; break;
                case "--text": raw = false; b64 = false; break;
                case "--help" or "-h": PrintHelp(); return 0;
                default:
                    if (arg.StartsWith('-')) { Fail($"未知参数：{arg}"); return 2; }
                    positional.Add(arg);
                    break;
            }
        }

        var path = positional.Count > 0 ? positional[0] : ".";

        try
        {
            using var client = BridgeClient.Create(sessionPath);

            if (command == "cat")
                return RunCat(client, path, offset, length, maxBytes, outFile, raw, b64);

            // CLI vocabulary (ls/cat) maps onto the bridge's operation names (list/read).
            var op = command == "ls" ? "list" : command;
            var dto = client.Call(new BridgeRequest(
                Op: op,
                Path: path,
                Glob: glob,
                Max: max,
                Depth: depth,
                Format: "json"));

            Console.Out.WriteLine(json ? RenderJson(dto) : RenderText(dto));
            return 0;
        }
        catch (BridgeException ex)
        {
            Fail(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("cli failed: " + ex);
            Fail(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Reads a whole file by default (in bounded chunks) instead of silently
    /// returning only the first block, which is what a plain `cat` should do.
    /// Informational messages go to stderr so stdout stays pure data.
    /// </summary>
    private static int RunCat(BridgeClient client, string path, long offset, int length,
        long? maxBytes, string? outFile, bool raw, bool b64)
    {
        // Printing to stdout is capped so an agent cannot dump a huge dataset into
        // its context by accident. Saving to a file is not capped: the caller
        // explicitly asked for that file, and --max-bytes can still bound it.
        var want = length > 0
            ? length
            : maxBytes is not null
                ? (maxBytes.Value <= 0 ? long.MaxValue : maxBytes.Value)
                : outFile is not null ? long.MaxValue : DefaultCatBytes;

        var chunks = new List<byte[]>();
        long total = 0, position = offset, size = 0;
        var eof = false;

        while (total < want)
        {
            var take = (int)Math.Min(RemoteFs.MaxReadLength, want - total);
            var dto = (ReadDto)client.Call(new BridgeRequest("read", path, position, take, Format: "json"));
            size = dto.Size;

            var bytes = Convert.FromBase64String(dto.Data);
            if (bytes.Length > 0)
            {
                chunks.Add(bytes);
                total += bytes.Length;
                position += bytes.Length;
            }

            eof = dto.Eof;
            if (bytes.Length == 0 || eof) break;
        }

        var all = new byte[total];
        var at = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, all, at, chunk.Length);
            at += chunk.Length;
        }

        if (outFile is not null)
        {
            File.WriteAllBytes(outFile, all);
            Console.Error.WriteLine(
                $"已保存 {all.Length} 字节到 {outFile}（远端文件共 {size} 字节，偏移 {offset}，" +
                $"{(eof ? "已到文件末尾" : "尚未读完，可用 --offset 继续或提高 --max-bytes")}）");
            return 0;
        }

        // Informational messages always go to stderr so stdout stays pure data.
        if (!eof)
        {
            Console.Error.WriteLine(
                $"注意：本次只读取了 {all.Length} 字节（远端文件共 {size} 字节）。" +
                "请用 --offset 继续读取，或用 --out 保存，或用 --max-bytes 0 读取整个文件。");
        }

        if (b64) { Console.Out.WriteLine(Convert.ToBase64String(all)); return 0; }
        if (raw) { Console.OpenStandardOutput().Write(all, 0, all.Length); return 0; }

        Console.Out.Write(Encoding.UTF8.GetString(all));
        return 0;
    }

    private static string RenderText(object dto) => TextRenderer.Render(new BridgeResult.Payload(dto));

    private static string RenderJson(object dto) => System.Text.Json.JsonSerializer.Serialize(
        dto, dto.GetType(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) return null;
        i++;
        return args[i];
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
    private static long ParseLong(string? value, long fallback) => long.TryParse(value, out var parsed) ? parsed : fallback;

    private static void Fail(string message) => Console.Error.WriteLine("error: " + message);

    private static void TrySetUtf8Output()
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch { /* Redirected or invalid console handle: keep the default. */ }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            SciBridge {BuildInfo.Version} — 机构服务器只读数据桥（命令行）

            用法：
              SciBridge.exe health                          检查本机桥是否已连接
              SciBridge.exe ls [路径] [--max N]             列出目录
              SciBridge.exe stat <路径>                     查看单个路径的属性
              SciBridge.exe cat <路径> [选项]               读取文件内容
              SciBridge.exe find <路径> --glob "*.nc" [选项] 按文件名查找

            cat 选项：
              --offset N        从第 N 字节开始（默认 0）
              --length N        最多读取 N 字节（默认读整个文件）
              --max-bytes N     最多读取 N 字节；0 表示不限
                                （默认：--out 保存整个文件；打印到标准输出时上限 8388608）
              --out <文件>      把原始字节保存到本机文件（适合大文件或二进制）
              --raw             原始字节写到标准输出
              --b64             以 base64 写到标准输出
              （默认按 UTF-8 文本输出，无法解码的字节会被替换）

            find 选项：
              --glob "模式"     文件名匹配模式，支持 * 和 ?
              --max N           最多返回多少条（默认 500）
              --depth N         向下搜索的目录层数，起点算第 0 层（默认 5，最大 12）

            通用选项：
              --json            以 JSON 输出（便于程序解析）
              --session <文件>  指定连接信息文件（默认自动查找）
              --help, --version

            自检（不打开窗口，直接连服务器把每个只读操作跑一遍）：
              SciBridge.exe --self-test [--host 地址] [--port 端口] [--user 账号] [--root 目录]

            只读保证：本工具只提供列目录、看属性、读文件、按名字查找。
            没有任何写入、删除、改名、上传或远程命令执行功能，且被限制在“可读根目录”内。

            路径都相对于“可读根目录”；"." 或 "/" 表示根目录本身，不支持 ".."。
            目录名以 "/" 结尾，符号链接名以 "@" 结尾（符号链接默认不会被跟随）。
            """);
    }
}
