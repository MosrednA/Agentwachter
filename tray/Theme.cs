using System.Drawing.Drawing2D;

namespace Agentwachter;

/// <summary>UsageTray's palette, so both trays look like one family.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x1C, 0x1D, 0x22);
    public static readonly Color Track = Color.FromArgb(0x30, 0x32, 0x3B);
    public static readonly Color Text = Color.FromArgb(0xEC, 0xEC, 0xF0);
    public static readonly Color Muted = Color.FromArgb(0x8B, 0x8E, 0x9A);
    public static readonly Color Ok = Color.FromArgb(0x4C, 0xC3, 0x8A);
    public static readonly Color Warn = Color.FromArgb(0xF0, 0xA4, 0x3A);
    public static readonly Color Hot = Color.FromArgb(0xEF, 0x5B, 0x52);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
