using SciBridge;

// Cross-platform host used by the automated tests. It exercises exactly the same
// Core library the Windows GUI uses, so the loopback HTTP/read-only behaviour is
// verified natively on Linux without needing a Windows machine.
//
//   SciBridge.TestHost serve --host H --port P --user U --password PW [--root R] ...
//
// It prints one READY line with the session JSON (including the loopback URL and
// token), then the exact handoff text the GUI would copy. Tests call the HTTP
// endpoint directly, which is also the only agent-facing transport.

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (argv.Length > 0 && argv[0] == "serve")
{
    return await ServeAsync(argv.Skip(1).ToArray());
}

Console.Error.WriteLine("usage: SciBridge.TestHost serve --host H --port P --user U --password PW [--root R]");
Console.Error.WriteLine("       [--known-hosts FILE] [--allow-symlinks] [--reject-hostkey]");
return 2;

static async Task<int> ServeAsync(string[] args)
{
    var host = "127.0.0.1";
    var user = "";
    var password = "";
    var root = ".";
    var knownHosts = "";
    var port = 22;
    var allowSymlinks = false;
    var autoAccept = true;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--host": host = args[++i]; break;
            case "--port": port = int.Parse(args[++i]); break;
            case "--user": user = args[++i]; break;
            case "--password": password = args[++i]; break;
            case "--root": root = args[++i]; break;
            case "--known-hosts": knownHosts = args[++i]; break;
            case "--allow-symlinks": allowSymlinks = true; break;
            case "--reject-hostkey": autoAccept = false; break;
            default:
                Console.Error.WriteLine($"未知参数：{args[i]}");
                return 2;
        }
    }

    var store = new HostKeyStore(string.IsNullOrEmpty(knownHosts) ? null : knownHosts);
    var interaction = new ScriptedInteraction(autoAccept);

    try
    {
        var fs = SshConnector.Connect(
            new ConnectRequest(host, port, user, password, root, allowSymlinks),
            store,
            interaction);

        using var bridge = BridgeHost.Start(fs, host, port, user);

        Console.WriteLine("READY " + bridge.Session.ToJson().Replace('\n', ' ').Replace('\r', ' '));
        Console.WriteLine("----- HANDOFF -----");
        Console.WriteLine(HandoffText.Generate(bridge.Session));
        Console.WriteLine("----- END -----");
        Console.Out.Flush();

        // Stay alive until the parent closes stdin (or the process is killed).
        while (Console.In.ReadLine() is not null) { }
        return 0;
    }
    catch (BridgeException ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

internal sealed class ScriptedInteraction(bool acceptHostKey) : IUserInteraction
{
    public HostKeyDecision ConfirmHostKey(HostKeyCheck check)
    {
        Console.Error.WriteLine($"hostkey {check.Verdict} {check.KeyType} {check.FingerprintSha256}");
        return acceptHostKey ? HostKeyDecision.AcceptAndPin : HostKeyDecision.Reject;
    }

    public string? AnswerChallenge(string instruction, string request, bool echoed) => null;
}
