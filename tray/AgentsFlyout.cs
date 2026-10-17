using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Agentwachter;

/// <summary>
/// Borderless flyout above the tray. One line per running session: tool, state, project, what it last
/// said, tokens per hour. Hover a line for its numbers; click a Claude line to open it in the desktop app.
/// </summary>
sealed class AgentsFlyout : Flyout
{
    // Layout in 96-dpi units.
    const int W = 420, Pad = 12, HeadH = 24, RowH = 20, FootH = 24, MaxRows = 10;
    const int TagW = 22, ChipW = 52, ProjectW = 80, TokensW = 40;

    IReadOnlyList<AgentSession>? _agents; // null: the watcher does not answer
    string? _error;
    DateTimeOffset? _updated;
    Point _trayPoint;
    Rectangle _footer, _overview;
    readonly List<(Rectangle Bounds, AgentSession Session)> _rows = [];
    readonly ToolTip _tip = new();
    AgentSession? _hover;

    public event Action? RefreshRequested;

    public void SetData(IReadOnlyList<AgentSession>? agents, DateTimeOffset? updated, string? error)
    {
        _agents = agents;
        _updated = updated;
        _error = error;
        if (!Visible) return;
        Relayout();
        Invalidate();
    }

    public void ShowNearTray()
    {
        _trayPoint = Cursor.Position;
        Relayout();
        PositionNearTray();
        Show();
        Activate();
    }

    void PositionNearTray()
    {
        var area = AvailableArea(Screen.FromPoint(_trayPoint));
        var margin = Px(10);
        var x = Math.Clamp(_trayPoint.X - Width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        var y = _trayPoint.Y <= area.Top ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, y);
    }

    /// <summary>Renders the flyout to a PNG without showing it (docs and layout checks).</summary>
    public void RenderTo(IReadOnlyList<AgentSession>? agents, string path, string? error = null)
    {
        _agents = agents;
        _error = error;
        _updated = DateTimeOffset.Now;
        Relayout();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _tip.Hide(this);
        HideOnDeactivate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        else if (e.KeyCode is Keys.F5 or Keys.R) RefreshRequested?.Invoke();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var row = RowAt(e.Location);
        Cursor = row?.Link is not null || _footer.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (!ReferenceEquals(row, _hover))
        {
            _hover = row;
            if (row is null) _tip.Hide(this);
            else _tip.Show(Details(row), this, e.X + Px(12), e.Y + Px(18));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = null;
        _tip.Hide(this);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (RowAt(e.Location)?.Link is { } link) Open("claude://claude.ai/epitaxy/" + link);
        else if (_overview.Contains(e.Location)) Open(AgentsSource.PageUrl);
        else if (_footer.Contains(e.Location)) RefreshRequested?.Invoke();
        base.OnMouseClick(e);
    }

    AgentSession? RowAt(Point p) => _rows.FirstOrDefault(r => r.Bounds.Contains(p)).Session;

    void Open(string url)
    {
        Hide();
        try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { } // no handler for the link: nothing to open
    }

    static string Details(AgentSession a) => string.Join("\n", new[]
    {
        a.Title,
        a.Summary,
        string.Join(" · ", new[]
        {
            $"{a.ToolsPerHour:0} " + L.T("tool calls/h", "tool calls/u"),
            $"{a.ErrorRate:0%} " + L.T("errors", "fouten"),
            $"{a.RepeatRatio:0%} " + L.T("repeats", "herhaald"),
            a.MinsSincePatch is { } m ? L.T($"last patch {m:0} min ago", $"laatste patch {m:0} min geleden") : null,
        }.Where(s => s is not null)),
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Every session that may need a look, then the latest finished ones up to MaxRows; the rest as a count.</summary>
    (List<AgentSession> Shown, int Hidden) VisibleAgents()
    {
        var all = _agents ?? [];
        var room = MaxRows - all.Count(a => !a.Done);
        var shown = all.Where(a => !a.Done || room-- > 0).ToList();
        return (shown, all.Count - shown.Count);
    }

    int Lines
    {
        get
        {
            if (_agents is null) return _error is null ? 1 : 2;
            if (_agents.Count == 0) return 1;
            var (shown, hidden) = VisibleAgents();
            return shown.Count + (hidden > 0 ? 1 : 0);
        }
    }

    protected override void Relayout()
    {
        Size = new Size(Px(W), Px(Pad + HeadH + Lines * RowH + 4 + FootH));
        if (Visible) PositionNearTray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var title = new Font("Segoe UI Semibold", Px(13), GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", Px(11.5f), GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", Px(12), GraphicsUnit.Pixel);

        int left = Px(Pad), right = Width - Px(Pad), y = Px(Pad), rowH = Px(RowH);
        _rows.Clear();
        _overview = Rectangle.Empty;

        const string name = "Agents";
        Label(g, name, title, Theme.Text, new Rectangle(left, y, right - left, Px(HeadH)));
        if (_agents is { Count: > 0 })
        {
            var nameW = TextRenderer.MeasureText(g, name, title, Size.Empty, TextFormatFlags.NoPadding).Width + Px(12);
            var counts = string.Join(" · ", _agents.GroupBy(a => Chip(a).Text).Select(s => $"{s.Count()} {s.Key}"));
            Label(g, counts, small, Theme.Muted, Rectangle.FromLTRB(left + nameW, y, right, y + Px(HeadH)), right: true);
        }
        y += Px(HeadH);

        if (_agents is null)
        {
            var state = _updated is null ? L.T("Starting…", "Starten…") : L.T("Watcher not running", "Watcher draait niet");
            Label(g, state, small, Theme.Warn, new Rectangle(left, y, right - left, rowH));
            y += rowH;
            if (_error is not null)
            {
                Label(g, _error, small, Theme.Muted, new Rectangle(left, y, right - left, rowH));
                y += rowH;
            }
        }
        else if (_agents.Count == 0)
        {
            Label(g, L.T("No sessions running", "Geen sessies actief"), small, Theme.Muted, new Rectangle(left, y, right - left, rowH));
            y += rowH;
        }
        else
        {
            var (shown, hidden) = VisibleAgents();
            foreach (var a in shown)
            {
                DrawRow(g, a, small, mono, left, right, y);
                _rows.Add((Rectangle.FromLTRB(0, y, Width, y + rowH), a));
                y += rowH;
            }
            if (hidden > 0)
            {
                Label(g, $"+{hidden} " + L.T("done", "klaar"), small, Theme.Muted, Rectangle.FromLTRB(left + Px(TagW), y, right, y + rowH));
                y += rowH;
            }
        }

        y += Px(4);
        using (var line = new Pen(Theme.Track, 1))
            g.DrawLine(line, left, y, right, y);
        _footer = new Rectangle(0, y, Width, Height - y);
        var footer = Rectangle.FromLTRB(left, y, right, y + Px(FootH));
        if (_updated is { } u)
            Label(g, $"{u:HH:mm} · " + L.T("click to refresh", "klik = verversen"), small, Theme.Muted, footer);
        if (_agents is not null)
        {
            var overview = L.T("overview ↗", "overzicht ↗");
            var w = TextRenderer.MeasureText(g, overview, small, Size.Empty, TextFormatFlags.NoPadding).Width;
            _overview = Rectangle.FromLTRB(right - w, y, right, y + Px(FootH));
            Label(g, overview, small, Theme.Muted, footer, right: true);
        }
    }

    void DrawRow(Graphics g, AgentSession a, Font small, Font mono, int left, int right, int y)
    {
        var rowH = Px(RowH);
        var x = left;
        Label(g, a.Tool == "codex" ? "cx" : "cl", mono, Theme.Muted, new Rectangle(x, y, Px(TagW), rowH));
        x += Px(TagW);

        var (text, color) = Chip(a);
        var chip = new Rectangle(x, y + Px(3), Px(ChipW), rowH - Px(6));
        using (var path = Theme.RoundRect(chip, chip.Height / 2f))
        using (var fill = new SolidBrush(color == Theme.Muted ? Theme.Track : Color.FromArgb(0x38, color)))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, text, small, chip, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        x += Px(ChipW + 6);

        Label(g, a.Project, small, Theme.Text, new Rectangle(x, y, Px(ProjectW), rowH));
        x += Px(ProjectW + 6);
        var tokensX = right - Px(TokensW);
        Label(g, a.Summary, small, Theme.Muted, Rectangle.FromLTRB(x, y, tokensX - Px(4), y + rowH));
        Label(g, Tokens(a.TokensPerHour), mono, Theme.Muted, new Rectangle(tokensX, y, Px(TokensW), rowH), right: true);
    }

    static (string Text, Color Color) Chip(AgentSession a) => a switch
    {
        { Circles: true } => (L.T("circles", "rondjes"), Theme.Hot),
        { State: "wacht op jou" } => (L.T("waiting", "wacht"), Theme.Warn),
        { State: "bezig" } => (L.T("busy", "bezig"), Theme.Ok),
        { State: "stil" } => (L.T("quiet", "stil"), Theme.Muted),
        { State: "dood" } => (L.T("dead", "dood"), Theme.Hot),
        _ => (L.T("done", "klaar"), Theme.Muted),
    };

    /// <summary>Tokens per hour, short: 850k, 2.4M, 31M.</summary>
    static string Tokens(double perHour) => perHour switch
    {
        <= 0 => "",
        >= 9.95e6 => $"{perHour / 1e6:0}M",
        >= 1e6 => $"{perHour / 1e6:0.0}M",
        _ => $"{perHour / 1e3:0}k",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
