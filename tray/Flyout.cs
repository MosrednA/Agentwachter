using System.Runtime.InteropServices;

namespace Agentwachter;

/// <summary>
/// Borderless, rounded, topmost tool window laid out in 96-dpi units. It is created once
/// and shown again and again, so it hides instead of closing.
/// </summary>
abstract class Flyout : Form
{
    protected Flyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    /// <summary>When it last lost focus and hid; the tray click that hid it must not reopen it.</summary>
    public DateTime LastHidden { get; private set; }

    protected void HideOnDeactivate()
    {
        Hide();
        LastHidden = DateTime.Now;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2; // DWMWCP_ROUND (Windows 11)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    // Alt+F4 would dispose the form, and the next opening would then throw.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        Hide();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    /// <summary>Sets the size (and, where the flyout owns it, the position) for the current data and DPI.</summary>
    protected abstract void Relayout();

    protected float DpiScale => DeviceDpi / 96f;
    protected int Px(float v) => (int)Math.Round(v * DpiScale);

    /// <summary>Keep flyouts clear of an autohide taskbar, which WorkingArea does not reserve.</summary>
    protected static Rectangle AvailableArea(Screen screen)
    {
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        for (uint edge = 0; edge < 4; edge++)
        {
            var data = new AppBarData
            {
                Size = (uint)Marshal.SizeOf<AppBarData>(),
                Edge = edge,
                Rect = new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
            };
            var taskbar = SHAppBarMessage(0xB /* ABM_GETAUTOHIDEBAREX */, ref data);
            if (taskbar == 0 || !GetWindowRect(taskbar, out var rect)) continue;

            // Its position moves offscreen when hidden; its width/height still gives the full thickness.
            area = edge switch
            {
                0 => Rectangle.FromLTRB(Math.Max(area.Left, bounds.Left + rect.Right - rect.Left), area.Top, area.Right, area.Bottom),
                1 => Rectangle.FromLTRB(area.Left, Math.Max(area.Top, bounds.Top + rect.Bottom - rect.Top), area.Right, area.Bottom),
                2 => Rectangle.FromLTRB(area.Left, area.Top, Math.Min(area.Right, bounds.Right - rect.Right + rect.Left), area.Bottom),
                _ => Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Min(area.Bottom, bounds.Bottom - rect.Bottom + rect.Top)),
            };
        }
        return area;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage, Edge;
        public NativeRect Rect;
        public nint Param;
    }

    protected static void Label(Graphics g, string text, Font font, Color color, Rectangle bounds, bool right = false)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
                  | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                  | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, font, bounds, color, flags);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shell32.dll")]
    static extern nint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(nint window, out NativeRect rect);
}
