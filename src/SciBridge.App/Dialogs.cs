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
