using SciBridge;

namespace SciBridge.App;

internal sealed class MainForm : Form
{
    private readonly HostKeyStore _hostKeys = new();
    private readonly Settings _settings = Settings.Load();

    private readonly TextBox _hostBox = new();
    private readonly TextBox _portBox = new();
    private readonly TextBox _userBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _rememberBox = new();
    private readonly TextBox _rootBox = new();
    private readonly CheckBox _symlinkBox = new();
    private readonly Button _connectButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _infoButton = new();
    private readonly Button _folderButton = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _handoffBox = new();

    private BridgeHost? _bridge;
    private bool _busy;
    private volatile bool _closing;

    public MainForm()
    {
        BuildUi();
        LoadSettingsIntoFields();
    }

    // ------------------------------------------------------------------- UI

    private void BuildUi()
    {
        Text = "SciBridge — 机构服务器只读数据桥";
        ClientSize = new Size(780, 760);
        MinimumSize = new Size(680, 620);
        StartPosition = FormStartPosition.CenterScreen;

        try { Font = new Font("Microsoft YaHei UI", 9F); }
        catch { /* Keep the default font if that family is unavailable. */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- title
        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            Text = "只读科研数据桥",
            Margin = new Padding(2, 0, 2, 2),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(730, 0),
            ForeColor = SystemColors.GrayText,
            Text = "在本机连接机构服务器，只把“列目录 / 看属性 / 读文件 / 按名字查找”交给 Codex。" +
                   "没有写入、删除、改名、上传，也没有远程命令。密码只用在这台电脑上。",
            Margin = new Padding(2, 0, 2, 10),
        };
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        root.Controls.Add(header, 0, 0);

        // ---- fields
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(fields, "服务器地址", _hostBox, "机构服务器的 IP 或域名，例如 172.16.1.170");
        AddRow(fields, "端口", _portBox, "SSH 端口，默认 22");
        AddRow(fields, "账号", _userBox, "你在服务器上的登录账号");
        AddRow(fields, "密码", _passwordBox, "只在本机使用；不勾选“记住密码”就不会写入磁盘");
        _passwordBox.UseSystemPasswordChar = true;

        _rememberBox.Text = "记住密码（用 Windows 加密，仅当前电脑用户可解密）";
        _rememberBox.AutoSize = true;
        AddFullWidthRow(fields, _rememberBox);

        AddRow(fields, "可读根目录", _rootBox,
            "Codex 能看到的最上层目录。填 . 表示账号主目录；也可以填 data/2024 这样的子目录。");
        _rootBox.Text = ".";

        _symlinkBox.Text = "允许跟随符号链接（可能越出上面的可读根目录，一般不要勾选）";
        _symlinkBox.AutoSize = true;
        AddFullWidthRow(fields, _symlinkBox);

        root.Controls.Add(fields, 0, 1);

        // ---- buttons
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 6),
        };

        _connectButton.Text = "连接";
        _connectButton.AutoSize = true;
        _connectButton.Padding = new Padding(14, 4, 14, 4);
        _connectButton.Click += OnConnectClicked;
        buttons.Controls.Add(_connectButton);

        _copyButton.Text = "复制交接文字";
        _copyButton.AutoSize = true;
        _copyButton.Padding = new Padding(10, 4, 10, 4);
        _copyButton.Enabled = false;
        _copyButton.Click += (_, _) => Dialogs.CopyToClipboard(this, _handoffBox.Text);
        buttons.Controls.Add(_copyButton);

        _infoButton.Text = "显示本机连接信息";
        _infoButton.AutoSize = true;
        _infoButton.Padding = new Padding(10, 4, 10, 4);
        _infoButton.Enabled = false;
        _infoButton.Click += (_, _) =>
        {
            if (_bridge is not null) Dialogs.ShowConnectionInfo(this, _bridge.Session);
        };
        buttons.Controls.Add(_infoButton);

        _folderButton.Text = "打开数据目录";
        _folderButton.AutoSize = true;
        _folderButton.Padding = new Padding(10, 4, 10, 4);
        _folderButton.Click += (_, _) => OpenDataFolder();
        buttons.Controls.Add(_folderButton);

        root.Controls.Add(buttons, 0, 2);

        // ---- status
        _statusLabel.AutoSize = true;
        _statusLabel.MaximumSize = new Size(730, 0);
        _statusLabel.Text = "未连接。填好服务器信息后点“连接”。";
        _statusLabel.Margin = new Padding(2, 0, 2, 6);
        root.Controls.Add(_statusLabel, 0, 3);

        // ---- handoff
        _handoffBox.Dock = DockStyle.Fill;
        _handoffBox.Multiline = true;
        _handoffBox.ReadOnly = true;
        _handoffBox.ScrollBars = ScrollBars.Vertical;
        _handoffBox.WordWrap = true;
        _handoffBox.BackColor = SystemColors.Window;
        _handoffBox.Text = "连接成功后，这里会出现一段“交接文字”。\r\n\r\n" +
                           "把它整段复制，粘贴到 Codex 的对话里，Codex 就知道该怎么读取数据了。";
        root.Controls.Add(_handoffBox, 0, 4);

        Controls.Add(root);
        AcceptButton = _connectButton;

        var tips = new ToolTip();
        tips.SetToolTip(_rootBox, "例如 . 或 data/2024。Codex 无法访问这个目录之外的内容。");
        tips.SetToolTip(_symlinkBox, "符号链接可能指向可读根目录之外。默认不跟随更安全。");
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control, string tooltip)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 6, 3),
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, 4);

        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);

        var tip = new ToolTip();
        tip.SetToolTip(control, tooltip);
        tip.SetToolTip(caption, tooltip);
    }

    /// <summary>Adds a checkbox (or similar) on its own full-width row.</summary>
    private static void AddFullWidthRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(3, 2, 3, 8);
        table.Controls.Add(control, 1, row);
    }

    // -------------------------------------------------------------- settings

    private void LoadSettingsIntoFields()
    {
        _hostBox.Text = _settings.Host;
        _portBox.Text = (_settings.Port > 0 ? _settings.Port : 22).ToString();
        _userBox.Text = _settings.User;
        _rootBox.Text = string.IsNullOrWhiteSpace(_settings.Root) ? "." : _settings.Root;
        _symlinkBox.Checked = _settings.AllowSymlinks;
        _rememberBox.Checked = _settings.RememberPassword;

        if (_settings.RememberPassword)
        {
            var restored = SecretStore.Unprotect(_settings.PasswordBlob);
            if (restored is not null) _passwordBox.Text = restored;
        }
    }

    private void SaveSettingsFromFields()
    {
        _settings.Host = _hostBox.Text.Trim();
        _settings.Port = int.TryParse(_portBox.Text.Trim(), out var port) ? port : 22;
        _settings.User = _userBox.Text.Trim();
        _settings.Root = string.IsNullOrWhiteSpace(_rootBox.Text) ? "." : _rootBox.Text.Trim();
        _settings.AllowSymlinks = _symlinkBox.Checked;
        _settings.RememberPassword = _rememberBox.Checked;
        _settings.PasswordBlob = _rememberBox.Checked ? SecretStore.Protect(_passwordBox.Text) : "";
        _settings.Save();
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "无法打开数据目录：" + ex.Message, "打开失败");
        }
    }

    // -------------------------------------------------------------- connect

    private void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_bridge is not null) { Disconnect(); return; }

        var host = _hostBox.Text.Trim();
        var user = _userBox.Text.Trim();
        var password = _passwordBox.Text;
        var root = _rootBox.Text.Trim();

        if (host.Length == 0) { Warn("请填写服务器地址。", _hostBox); return; }
        if (user.Length == 0) { Warn("请填写账号。", _userBox); return; }
        if (password.Length == 0) { Warn("请填写密码。", _passwordBox); return; }
        if (!int.TryParse(_portBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Warn("端口必须是 1 到 65535 之间的数字。", _portBox);
            return;
        }
        if (root.Contains(".."))
        {
            Warn("可读根目录里不能包含 “..”，请填写绝对路径或相对于账号主目录的路径。", _rootBox);
            return;
        }

        SaveSettingsFromFields();

        var request = new ConnectRequest(host, port, user, password, root, _symlinkBox.Checked);

        SetBusy(true);
        SetStatus("正在连接并验证服务器身份…");
        _handoffBox.Text = "正在连接，请稍候…";

        Task.Run(() =>
        {
            try
            {
                var fs = SshConnector.Connect(request, _hostKeys, new UiInteraction(this));
                var bridge = BridgeHost.Start(fs, host, port, user);
                PostToUi(() => OnConnected(bridge));
            }
            catch (Exception ex)
            {
                var message = ex is BridgeException ? ex.Message : SshConnector.Describe(ex, request);
                PostToUi(() => OnConnectFailed(message));
            }
        });
    }

    private void OnConnected(BridgeHost bridge)
    {
        if (_closing)
        {
            bridge.Dispose();
            return;
        }

        _bridge = bridge;
        SetBusy(false);
        _connectButton.Text = "断开";
        _copyButton.Enabled = true;
        _infoButton.Enabled = true;
        SetInputsEnabled(false);

        var scopeText = string.IsNullOrWhiteSpace(bridge.Session.Root) || bridge.Session.Root == "."
            ? "账号主目录"
            : bridge.Session.Root;

        SetStatus($"已连接（只读）：{bridge.Session.User}@{bridge.Session.Host}:{bridge.Session.Port}　" +
                  $"可读范围：{scopeText}。把下面的交接文字发给 Codex 即可开始。关闭窗口会断开连接。");

        _handoffBox.Text = HandoffText.Generate(bridge.Session, bridge.SessionPath);
        _handoffBox.SelectionStart = 0;
        _handoffBox.SelectionLength = 0;

        Dialogs.CopyToClipboard(this, _handoffBox.Text);
        Dialogs.Info(this,
            "已连接（只读）。\n\n" +
            "交接文字已经复制到剪贴板，直接粘贴到 Codex 的对话里即可。\n\n" +
            "如果之后还想再复制一次，点窗口里的“复制交接文字”。\n" +
            "关闭这个窗口就会断开连接。",
            "连接成功");
    }

    private void OnConnectFailed(string message)
    {
        if (_closing) return;
        SetBusy(false);
        SetStatus("连接失败。请核对信息后重试；程序不会自动重试。");
        _handoffBox.Text = "连接失败。\r\n\r\n" + message;
        Dialogs.Error(this, message, "连接失败");
    }

    private void Disconnect()
    {
        try { _bridge?.Dispose(); }
        catch (Exception ex) { Log.Write("disconnect failed: " + ex.Message); }
        _bridge = null;

        _connectButton.Text = "连接";
        _copyButton.Enabled = false;
        _infoButton.Enabled = false;
        SetInputsEnabled(true);
        SetStatus("已断开。需要时重新点“连接”。");
        _handoffBox.Text = "已断开连接。";
    }

    // ----------------------------------------------------------------- misc

    private void Warn(string message, Control focus)
    {
        Dialogs.Error(this, message, "信息不完整");
        focus.Focus();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _connectButton.Enabled = !busy;
        _folderButton.Enabled = !busy;
        if (busy) _connectButton.Text = "正在连接…";
    }

    private void SetInputsEnabled(bool enabled)
    {
        _hostBox.Enabled = enabled;
        _portBox.Enabled = enabled;
        _userBox.Enabled = enabled;
        _passwordBox.Enabled = enabled;
        _rememberBox.Enabled = enabled;
        _rootBox.Enabled = enabled;
        _symlinkBox.Enabled = enabled;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void PostToUi(Action action)
    {
        if (_closing || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (Exception) { /* The window went away while connecting. */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_bridge is not null && e.CloseReason == CloseReason.UserClosing)
        {
            var confirmed = Dialogs.Confirm(this,
                "关闭窗口会断开与服务器的连接，Codex 也会立即失去访问。\n\n确定要关闭吗？",
                "确认断开");
            if (!confirmed)
            {
                e.Cancel = true;
                return;
            }
        }

        _closing = true;
        try { _bridge?.Dispose(); } catch { /* best effort */ }
        _bridge = null;
        base.OnFormClosing(e);
    }

    /// <summary>Marshals SSH-thread questions onto the UI thread.</summary>
    private sealed class UiInteraction(MainForm form) : IUserInteraction
    {
        public HostKeyDecision ConfirmHostKey(HostKeyCheck check)
        {
            var result = form.Invoke(new Func<HostKeyDecision>(() => Dialogs.AskHostKey(form, check)));
            return result is HostKeyDecision decision ? decision : HostKeyDecision.Reject;
        }

        public string? AnswerChallenge(string instruction, string request, bool echoed)
        {
            var result = form.Invoke(new Func<string?>(
                () => Dialogs.AskChallenge(form, instruction, request, echoed)));
            return result as string;
        }
    }
}
