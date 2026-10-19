using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Agentwachter;

/// <summary>
/// A dot in the color of the session that most needs a look, with the number of sessions that are not done.
/// An empty ring while the watcher does not answer.
/// </summary>
static class TrayIcon
{
    public static Icon Render(IReadOnlyList<AgentSession>? agents)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var dot = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
            if (agents is null)
            {
                using var ring = new Pen(Theme.Muted, Math.Max(1.5f, size / 9f));
                dot.Inflate(-ring.Width / 2, -ring.Width / 2);
                g.DrawEllipse(ring, dot);
            }
            else
            {
                using (var fill = new SolidBrush(Worst(agents)))
                    g.FillEllipse(fill, dot);
                var open = agents.Count(a => !a.Done);
                if (open > 0)
                {
                    using var font = new Font("Segoe UI", size * (open > 9 ? 0.48f : 0.66f), FontStyle.Bold, GraphicsUnit.Pixel);
                    using var ink = new SolidBrush(Theme.Bg);
                    using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(Math.Min(open, 99).ToString(), font, ink, dot, center);
                }
            }
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    /// <summary>Red: going in circles or dead. Orange: waiting for you. Green: working. Grey: all done.</summary>
    public static Color Worst(IReadOnlyList<AgentSession> agents) =>
        agents.Any(a => a.Circles || a.State == "dead" || a.Subagents.Any(s => s.Circles)) ? Theme.Hot
        : agents.Any(a => a.State == "waiting") ? Theme.Warn
        : agents.Any(a => !a.Done) ? Theme.Ok
        : Theme.Muted;

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
