using System.Diagnostics;
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
    private readonly CheckBox _readBox = new();
    private readonly CheckBox _writeBox = new();
    private readonly CheckBox _uploadBox = new();
    private readonly CheckBox _mkdirBox = new();
    private readonly Label _writeWarning = new();
    private readonly Button _connectButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _folderButton = new();
    private readonly Button _auditButton = new();
    private readonly Label _statusLabel = new();
    private readonly Label _versionLabel = new();
    private readonly LinkLabel _repoLink = new();
    private readonly TextBox _handoffBox = new();
    private readonly bool _checkUpdates;

    private BridgeHost? _bridge;
    private bool _busy;
    private bool _closingForUpdate;
    private bool _updateCheckStarted;
    private volatile bool _closing;

    public MainForm(bool checkUpdates = true)
    {
        _checkUpdates = checkUpdates;
        BuildUi();
        LoadSettingsIntoFields();
    }

    // ------------------------------------------------------------------- UI

    private void BuildUi()
    {
        Text = "SciBridge — 机构服务器科研数据桥（默认只读）";
        ClientSize = new Size(780, 760);
        MinimumSize = new Size(680, 620);
        StartPosition = FormStartPosition.CenterScreen;

        try { Font = new Font("Microsoft YaHei UI", 9F); }
        catch { /* Keep the default font if that family is unavailable. */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- title
        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            Text = "科研数据桥（默认只读）",
            Margin = new Padding(2, 0, 2, 2),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(730, 0),
            ForeColor = SystemColors.GrayText,
            Text = "在本机连接机构服务器。默认只读；需要时可按需开启“仅新建文件/目录”的写权限。" +
                   "没有覆盖、删除、改名或远程命令。密码只用在这台电脑上。",
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

        _readBox.Text = "允许读取（始终开启）";
        _readBox.AutoSize = true;
        _readBox.Checked = true;
        _readBox.Enabled = false;
        AddFullWidthRow(fields, _readBox);

        _writeBox.Text = "允许写入（仅新建文件/目录，不能覆盖或删除）";
        _writeBox.AutoSize = true;
        _writeBox.CheckedChanged += (_, _) => UpdateWriteOptions();
        AddFullWidthRow(fields, _writeBox);

        var writeOptions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(3, 2, 3, 8),
        };
        _uploadBox.Text = "上传新文件";
        _uploadBox.AutoSize = true;
        _uploadBox.Checked = true;
        _uploadBox.Enabled = false;
        _mkdirBox.Text = "创建目录";
        _mkdirBox.AutoSize = true;
        _mkdirBox.Checked = true;
        _mkdirBox.Enabled = false;
        writeOptions.Controls.Add(_uploadBox);
        writeOptions.Controls.Add(_mkdirBox);
        AddFullWidthRow(fields, writeOptions);

        _writeWarning.AutoSize = true;
        _writeWarning.MaximumSize = new Size(600, 0);
        _writeWarning.ForeColor = Color.Firebrick;
        _writeWarning.Text = "风险：写权限会让 Codex 在服务器上新建文件和目录；本版本不能覆盖、重命名或删除。";
        _writeWarning.Visible = false;
        AddFullWidthRow(fields, _writeWarning);

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

        _folderButton.Text = "打开数据目录";
        _folderButton.AutoSize = true;
        _folderButton.Padding = new Padding(10, 4, 10, 4);
        _folderButton.Click += (_, _) => OpenDataFolder();
        buttons.Controls.Add(_folderButton);

        _auditButton.Text = "打开审计日志";
        _auditButton.AutoSize = true;
        _auditButton.Padding = new Padding(10, 4, 10, 4);
        _auditButton.Click += (_, _) => OpenAuditFolder();
        buttons.Controls.Add(_auditButton);

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
                           "把它整段复制，粘贴到 Codex 的对话里；里面有本机 HTTP 地址和短期令牌，" +
                           "Codex 不需要执行本程序。";
        root.Controls.Add(_handoffBox, 0, 4);

        // ---- footer: repository link + version (both embedded at build time)
        _versionLabel.AutoSize = true;
        _versionLabel.Anchor = AnchorStyles.Right;
        _versionLabel.TextAlign = ContentAlignment.MiddleRight;
        _versionLabel.ForeColor = SystemColors.GrayText;
        _versionLabel.Margin = new Padding(2, 6, 2, 0);
        _versionLabel.Text = BuildInfo.DisplayVersion;

        _repoLink.AutoSize = true;
        _repoLink.Anchor = AnchorStyles.Left;
        _repoLink.Text = "GitHub 仓库";
        _repoLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _repoLink.LinkColor = Color.FromArgb(31, 111, 235);
        _repoLink.Margin = new Padding(2, 6, 2, 0);
        _repoLink.LinkClicked += (_, _) => OpenRepository();
        _repoLink.Visible = !string.IsNullOrWhiteSpace(BuildInfo.RepositoryUrl);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_repoLink, 0, 0);
        footer.Controls.Add(_versionLabel, 1, 0);
        root.Controls.Add(footer, 0, 5);

        Controls.Add(root);
        AcceptButton = _connectButton;
        Shown += (_, _) => StartUpdateCheck();

        var tips = new ToolTip();
        tips.SetToolTip(_rootBox, "例如 . 或 data/2024。Codex 无法访问这个目录之外的内容。");
        tips.SetToolTip(_symlinkBox, "符号链接可能指向可读根目录之外。默认不跟随更安全。");
        tips.SetToolTip(_writeBox, "只允许新建文件/目录；不会覆盖、重命名或删除服务器上的任何内容。");
        tips.SetToolTip(_writeWarning, "写操作会记录在审计日志中。");
        tips.SetToolTip(_versionLabel, "版本来自构建时的 release tag；本地非发布构建显示 0.0.0-dev。");
        if (!string.IsNullOrWhiteSpace(BuildInfo.RepositoryUrl))
            tips.SetToolTip(_repoLink, BuildInfo.RepositoryUrl);
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
        _writeBox.Checked = _settings.AllowWrite;
        _uploadBox.Checked = _settings.AllowUpload;
        _mkdirBox.Checked = _settings.AllowMkdir;
        _rememberBox.Checked = _settings.RememberPassword;
        UpdateWriteOptions();

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
        _settings.AllowWrite = _writeBox.Checked;
        _settings.AllowUpload = _uploadBox.Checked;
        _settings.AllowMkdir = _mkdirBox.Checked;
        _settings.RememberPassword = _rememberBox.Checked;
        _settings.PasswordBlob = _rememberBox.Checked ? SecretStore.Protect(_passwordBox.Text) : "";
        _settings.Save();
    }

    private void UpdateWriteOptions()
    {
        var enabled = _writeBox.Checked;
        _uploadBox.Enabled = enabled;
        _mkdirBox.Enabled = enabled;
        _writeWarning.Visible = enabled;
    }

    private void OpenAuditFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AuditDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.AuditDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "无法打开审计日志目录：" + ex.Message, "打开失败");
        }
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

        var allowWrite = _writeBox.Checked;
        var allowUpload = allowWrite && _uploadBox.Checked;
        var allowMkdir = allowWrite && _mkdirBox.Checked;
        if (allowWrite && !allowUpload && !allowMkdir)
        {
            Warn("已勾选“允许写入”，但请至少选择“上传新文件”或“创建目录”。", _uploadBox);
            return;
        }
        if (allowWrite && !Dialogs.Confirm(this,
                "将允许 Codex 在服务器上新建文件和目录。\n\n" +
                "本版本不能覆盖、重命名或删除任何已有内容；所有操作会记录到审计日志。\n\n" +
                "确定开启写权限吗？", "确认开启写入"))
        {
            return;
        }

        SaveSettingsFromFields();

        var request = new ConnectRequest(host, port, user, password, root, _symlinkBox.Checked,
            allowUpload, allowMkdir);

        SetBusy(true);
        SetStatus("正在连接并验证服务器身份…");
        _handoffBox.Text = "正在连接，请稍候…";

        Task.Run(() =>
        {
            try
            {
                var fs = SshConnector.Connect(request, _hostKeys, new UiInteraction(this));
                var bridge = BridgeHost.Start(fs, host, port, user, allowUpload, allowMkdir);
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
        SetInputsEnabled(false);

        var scopeText = string.IsNullOrWhiteSpace(bridge.Session.Root) || bridge.Session.Root == "."
            ? "账号主目录"
            : bridge.Session.Root;

        var mode = bridge.Session.AllowUpload || bridge.Session.AllowMkdir ? "读写，仅新建" : "只读";
        SetStatus($"已连接（{mode}）：{bridge.Session.User}@{bridge.Session.Host}:{bridge.Session.Port}　" +
                  $"可读范围：{scopeText}。把下面的交接文字发给 Codex 即可开始。关闭窗口会断开连接。");

        _handoffBox.Text = HandoffText.Generate(bridge.Session);
        _handoffBox.SelectionStart = 0;
        _handoffBox.SelectionLength = 0;

        Dialogs.CopyToClipboard(this, _handoffBox.Text);
        Dialogs.Info(this,
            $"已连接（{mode}）。\n\n" +
            "交接文字已经复制到剪贴板，直接粘贴到 Codex 的对话里即可。\n" +
            "里面有本机 HTTP 地址和短期令牌：只在这台电脑上有效，不要外发。\n\n" +
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
        _writeBox.Enabled = enabled;
        _uploadBox.Enabled = enabled && _writeBox.Checked;
        _mkdirBox.Enabled = enabled && _writeBox.Checked;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void PostToUi(Action action)
    {
        if (_closing || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (Exception) { /* The window went away while connecting. */ }
    }

    // ------------------------------------------------------------- update
    private void StartUpdateCheck()
    {
        if (!_checkUpdates || _updateCheckStarted || _closing) return;
        _updateCheckStarted = true;

        // Development builds (0.0.0-dev) do not auto-update.
        if (!ReleaseVersion.TryParse(BuildInfo.Version, out _)) return;
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync(BuildInfo.Version, CancellationToken.None);
            if (info is null || _closing || IsDisposed) return;

            if (IsHandleCreated)
                BeginInvoke(new Action(() => PromptForUpdate(info)));
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
        }
    }

    private void PromptForUpdate(UpdateInfo info)
    {
        if (_closing || _closingForUpdate) return;

        var text =
            $"发现新版本 {info.Version}（当前 {BuildInfo.Version}）。\n\n" +
            "更新会关闭当前 SciBridge 连接，并在下载校验完成后重启程序。\n\n" +
            "是否下载并更新？";

        if (MessageBox.Show(this, text, "发现新版本",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            // "稍后" is only for this session: the next launch checks again.
            return;
        }

        if (!CanWriteInstallDirectory())
        {
            MessageBox.Show(this,
                "当前程序目录不可写（可能在 Program Files 或只读位置）。\n\n" +
                "请把 SciBridge.exe 放到你有写权限的文件夹后重新启动。",
                "无法更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DownloadAndApply(info);
    }

    private static bool CanWriteInstallDirectory()
    {
        var exe = Environment.ProcessPath;
        var dir = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(dir)) return false;

        var probe = Path.Combine(dir, ".scibridge-write-test");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            return false;
        }
    }

    private void DownloadAndApply(UpdateInfo info)
    {
        using var progress = new UpdateProgressForm(info);
        var result = progress.ShowDialog(this);

        if (result != DialogResult.OK || string.IsNullOrWhiteSpace(progress.DownloadedPath))
        {
            if (!string.IsNullOrWhiteSpace(progress.Error))
            {
                MessageBox.Show(this, "更新下载失败：\n\n" + progress.Error,
                    "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        ApplyUpdate(progress.DownloadedPath);
    }

    private void ApplyUpdate(string stagedExe)
    {
        var installExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(installExe) || !File.Exists(installExe))
        {
            MessageBox.Show(this, "找不到当前程序路径，无法安装更新。",
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var installDir = Path.GetDirectoryName(installExe);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show(this, "找不到当前程序目录，无法安装更新。",
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(stagedExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir,
            };
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--install-exe");
            startInfo.ArgumentList.Add(installExe);
            startInfo.ArgumentList.Add("--staged-exe");
            startInfo.ArgumentList.Add(stagedExe);

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("无法启动更新助手。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法启动更新：\n\n" + ex.Message,
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The helper waits for this process to exit, then swaps the exe and
        // starts the new version. Do not ask the usual "close connection?"
        // question during this deliberate restart.
        _closingForUpdate = true;
        try { _bridge?.Dispose(); } catch { /* best effort */ }
        _bridge = null;
        Close();
    }

    private void OpenRepository()
    {
        var url = BuildInfo.RepositoryUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "仓库链接无效。", "无法打开链接",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开浏览器：\n\n" + ex.Message,
                "打开链接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closingForUpdate)
        {
            _closing = true;
            try { _bridge?.Dispose(); } catch { /* best effort */ }
            _bridge = null;
            base.OnFormClosing(e);
            return;
        }

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
