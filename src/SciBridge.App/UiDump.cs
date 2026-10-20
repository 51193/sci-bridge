using System.Text;

namespace SciBridge.App;

/// <summary>
/// Lays the main window out off-screen and prints every control's real geometry.
/// This makes the layout verifiable by a script (no screenshots, no human eyes):
/// it catches zero-sized controls, text clipped by its own bounds, and controls
/// that fall outside the client area.
/// </summary>
internal static class UiDump
{
    public static int Run()
    {
        var exitCode = 0;
        try
        {
            // Match the CLI so Chinese labels survive redirection.
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console */ }
            using var form = new MainForm(checkUpdates: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            };

            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            Application.DoEvents();

            var output = new StringBuilder();
            output.AppendLine($"FORM|{form.Text}|client={form.ClientSize.Width}x{form.ClientSize.Height}");
            Walk(form, form, output, 0);

            Console.Out.Write(output.ToString());
            Console.Out.Flush();

            form.Hide();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("dump-ui failed: " + ex);
            exitCode = 1;
        }

        return exitCode;
    }

    private static void Walk(Control parent, Form form, StringBuilder output, int depth)
    {
        foreach (Control child in parent.Controls)
        {
            var screen = child.RectangleToScreen(child.ClientRectangle);
            var outside = screen.Right > form.RectangleToScreen(form.ClientRectangle).Right + 2 ||
                          screen.Bottom > form.RectangleToScreen(form.ClientRectangle).Bottom + 2;

            var text = (child.Text ?? "").Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 46) text = text[..46] + "…";

            output.AppendLine(string.Join('|',
                new string(' ', depth * 2) + child.GetType().Name,
                $"{child.Left},{child.Top},{child.Width},{child.Height}",
                $"pref={child.PreferredSize.Width}x{child.PreferredSize.Height}",
                child.AutoSize ? "autosize" : "fixed",
                child.Visible ? "visible" : "hidden",
                outside ? "OUTSIDE" : "inside",
                $"min={child.MinimumSize.Width}x{child.MinimumSize.Height}",
                text));

            Walk(child, form, output, depth + 1);
        }
    }
}
