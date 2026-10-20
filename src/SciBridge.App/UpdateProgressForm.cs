using SciBridge;

namespace SciBridge.App;

/// <summary>Modal progress window for the download+verify step.</summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly UpdateInfo _info;
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly Button _cancel = new();
    private readonly CancellationTokenSource _cts = new();

    public string? DownloadedPath { get; private set; }
    public string? Error { get; private set; }

    public UpdateProgressForm(UpdateInfo info)
    {
        _info = info;
        Text = "下载更新";
        ClientSize = new Size(460, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ControlBox = false;

        _status.SetBounds(16, 16, 428, 40);
        _status.Text = $"正在下载 SciBridge {_info.Version}…";

        _bar.SetBounds(16, 64, 428, 22);
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        _bar.Style = ProgressBarStyle.Continuous;

        _cancel.Text = "取消";
        _cancel.SetBounds(364, 102, 80, 30);
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "正在取消…";
            _cts.Cancel();
        };

        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(_cancel);
        Shown += OnShown;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        try
        {
            var progress = new Progress<double>(fraction =>
            {
                if (IsDisposed) return;
                _bar.Value = Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
                _status.Text = fraction >= 1
                    ? "下载完成，正在校验…"
                    : $"正在下载 SciBridge {_info.Version}… {fraction:P0}";
            });

            DownloadedPath = await UpdateService.DownloadAndVerifyAsync(_info, progress, _cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            DialogResult = DialogResult.Abort;
        }
        finally
        {
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }
}
