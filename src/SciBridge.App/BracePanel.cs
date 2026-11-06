namespace SciBridge.App;

/// <summary>Small vertical+branch guide that marks collapsible child options.</summary>
internal sealed class BracePanel : Control
{
    public BracePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Height <= 2) return;

        var x = 8;
        var mid = Height / 2;
        using var pen = new Pen(SystemColors.ControlDark, 1f);
        e.Graphics.DrawLine(pen, x, 0, x, Height);
        e.Graphics.DrawLine(pen, x, mid, Math.Max(x + 1, Width - 2), mid);
    }
}
