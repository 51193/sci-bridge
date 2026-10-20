using SciBridge;

// Cross-platform host used by the automated tests. It exercises exactly the same
// Core library the Windows GUI uses, so the loopback/pipe/read-only behaviour is
// verified natively on Linux without needing a Windows machine.
//
//   SciBridge.TestHost serve --host H --port P --user U --password PW [--root R] ...
//   SciBridge.TestHost <any CLI arguments>

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (argv.Length > 0 && argv[0] == "serve")
{
    return await ServeAsync(argv.Skip(1).ToArray());
}

return CliRunner.TryRun(argv) ?? 0;

static async Task<int> ServeAsync(string[] args)
{
    var host = "127.0.0.1";
    var user = "";
    var password = "";
    var root = ".";
    var sessionPath = "";
    var knownHosts = "";
    var exePath = "";
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
            case "--session": sessionPath = args[++i]; break;
            case "--known-hosts": knownHosts = args[++i]; break;
            case "--exe-path": exePath = args[++i]; break;
            case "--allow-symlinks": allowSymlinks = true; break;
            case "--reject-hostkey": autoAccept = false; break;
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

        using var bridge = BridgeHost.Start(
            fs, host, port, user,
            string.IsNullOrEmpty(sessionPath) ? null : sessionPath,
            string.IsNullOrEmpty(exePath) ? null : exePath);

        Console.WriteLine("READY " + bridge.Session.ToJson().Replace('\n', ' ').Replace('\r', ' '));
        Console.WriteLine("----- HANDOFF -----");
        Console.WriteLine(HandoffText.Generate(bridge.Session, bridge.SessionPath));
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
