namespace SciBridge.App;

/// <summary>Small modal dialogs that need an answer from the human before SSH can continue.</summary>
internal static class Dialogs
{
    /// <summary>
    /// Trust-on-first-use prompt. The fingerprint is shown in full and the user is
    /// told to verify it with their administrator; a changed key is presented as a
    /// warning rather than a routine confirmation.
    /// </summary>
    public static HostKeyDecision AskHostKey(IWin32Window owner, HostKeyCheck check)
    {
        var changed = check.Verdict == HostKeyVerdict.Changed;

        using var form = new Form
        {
            Text = changed ? "警告：服务器主机公钥已变化" : "首次连接：确认服务器身份",
            ClientSize = new Size(600, 340),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };

        var explanation = new Label
        {
            Dock = DockStyle.Top,
            Height = 118,
            Padding = new Padding(12, 12, 12, 0),
            Text = changed
                ? "这台服务器之前使用过另一个主机公钥。这可能表示服务器被重装过，也可能表示有人正在冒充服务器。\n\n" +
                  "请先通过可信渠道（电话或当面）向机构管理员核对下面的新指纹。在核对清楚之前，请点“取消”。"
                : "这是第一次连接，需要确认服务器身份（主机公钥）。\n\n" +
                  "如果机构管理员给过你服务器指纹，请逐字核对；没有给过就点“接受并记住”，本机会把它保存下来，" +
                  "以后同一台服务器如果换了公钥会立刻告警。",
        };

        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join(Environment.NewLine, new[]
            {
                $"服务器      : {check.Host}:{check.Port}",
                $"公钥类型    : {check.KeyType}",
                changed ? $"已保存指纹  : {check.PinnedFingerprintSha256}" : "",
                $"本次指纹    : {check.FingerprintSha256}",
            }.Where(line => line.Length > 0)),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12),
        };

        var decision = HostKeyDecision.Reject;
        var cancel = new Button { Text = "取消（推荐先核实）", DialogResult = DialogResult.Cancel, AutoSize = true };
        cancel.Click += (_, _) => decision = HostKeyDecision.Reject;
        buttons.Controls.Add(cancel);

        var once = new Button { Text = "仅本次使用", AutoSize = true };
        once.Click += (_, _) => { decision = HostKeyDecision.UseOnce; form.DialogResult = DialogResult.OK; };
        buttons.Controls.Add(once);

        var accept = new Button
        {
            Text = changed ? "我已核实，替换为新指纹" : "接受并记住",
            AutoSize = true,
        };
        accept.Click += (_, _) => { decision = HostKeyDecision.AcceptAndPin; form.DialogResult = DialogResult.OK; };
        buttons.Controls.Add(accept);

        form.Controls.Add(details);
        form.Controls.Add(buttons);
        form.Controls.Add(explanation);
        form.AcceptButton = changed ? cancel : accept;
        form.CancelButton = cancel;

        form.ShowDialog(owner);
        return decision;
    }

    /// <summary>Answers a keyboard-interactive prompt, e.g. a one-time code.</summary>
    public static string? AskChallenge(IWin32Window owner, string instruction, string request, bool echoed)
    {
        using var form = new Form
        {
            Text = "服务器要求补充信息",
            ClientSize = new Size(460, echoed ? 200 : 240),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };

        var text = string.IsNullOrWhiteSpace(instruction)
            ? "服务器要求你回答下面的问题才能继续登录。"
            : instruction;

        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = echoed ? 74 : 110,
            Padding = new Padding(12, 12, 12, 0),
            Text = text + "\n\n" + (string.IsNullOrWhiteSpace(request) ? "请输入：" : request),
        };

        var input = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = !echoed };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12),
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        form.Controls.Add(input);
        form.Controls.Add(buttons);
        form.Controls.Add(label);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => input.Focus();

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }

    /// <summary>
    /// Shows the loopback URL and token. This is the advanced path for tools that
    /// want to call the HTTP API directly instead of using the CLI.
    /// </summary>
    public static void ShowConnectionInfo(IWin32Window owner, SessionInfo session)
    {
        using var form = new Form
        {
            Text = "本机连接信息（不要外发）",
            ClientSize = new Size(620, 330),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(12, 12, 12, 0),
            Text =
                "下面的地址和令牌只在这台电脑上有效，而且只提供只读访问，关闭 SciBridge 后立即失效。\n\n" +
                "一般不需要用到它：让 Codex 直接用命令行（SciBridge.exe ls / cat）就够了。\n" +
                "只有在确有必要时才把它交给本机工具，不要发给别人或贴到网上。",
        };

        var body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join(Environment.NewLine, new[]
            {
                $"地址   : {session.Url}",
                $"令牌   : {session.Token}",
                $"管道   : {session.Pipe}",
                $"服务器 : {session.User}@{session.Host}:{session.Port}",
                $"范围   : {session.Root}",
                "",
                "示例（PowerShell）：",
                $"curl.exe \"{session.Url}/{session.Token}/list?path=.\"",
                $"curl.exe \"{session.Url}/{session.Token}/read?path=data%2Fsample.txt&length=4096\"",
            }),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12),
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, AutoSize = true };
        var copy = new Button { Text = "复制", AutoSize = true };
        copy.Click += (_, _) => CopyToClipboard(form, body.Text);
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);

        form.Controls.Add(body);
        form.Controls.Add(buttons);
        form.Controls.Add(note);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.ShowDialog(owner);
    }

    public static bool Confirm(IWin32Window owner, string text, string caption)
        => MessageBox.Show(owner, text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
           == DialogResult.OK;

    public static void Info(IWin32Window owner, string text, string caption)
        => MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Error(IWin32Window owner, string text, string caption)
        => MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>The clipboard can be briefly locked by another process; retry once.</summary>
    public static void CopyToClipboard(IWin32Window owner, string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(120);
            }
        }
        Error(owner, "复制失败：剪贴板暂时被其它程序占用，请稍后再试。", "复制失败");
    }
}
