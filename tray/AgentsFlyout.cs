using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Agentwachter;

/// <summary>
/// Borderless flyout above the tray. One line per running session: tool, state, project, what it last
/// said, tokens per hour, with its running subagents on a tree line under it. Hover a line for its numbers;
/// click a Claude line to open its session in the desktop app.
/// </summary>
sealed class AgentsFlyout : Flyout
{
    // Layout in 96-dpi units.
    const int W = 420, Pad = 12, HeadH = 24, RowH = 20, FootH = 24, MaxRows = 10;
    const int TagW = 22, IconW = 14, ChipW = 52, ProjectW = 80, TokensW = 40;

    /// <summary>The tools' taskbar icons, embedded from the watcher's icons folder.</summary>
    static readonly Dictionary<string, Bitmap> Icons = new[] { "claude", "codex" }.ToDictionary(tool => tool, tool =>
    {
        using var stream = typeof(AgentsFlyout).Assembly.GetManifestResourceStream(tool + ".png")!;
        using var image = Image.FromStream(stream);
        return new Bitmap(image); // a copy, so the stream can close
    });

    enum Tree { None, Mid, End }

    static readonly Color TreeLine = Color.FromArgb(0x53, 0x55, 0x5E); // Muted at half strength on Bg, but opaque so segments don't add up

    /// <summary>A session (Tree None), one of its running subagents, or a note closing its subagents (Agent null).</summary>
    readonly record struct Line(AgentSession? Agent, Tree Tree, string? Note = null);

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
            $"{a.ToolsPerHour:0} tool calls/h",
            $"{a.ErrorRate:0%} errors",
            $"{a.RepeatRatio:0%} repeats",
            a.MinsSincePatch is { } m ? $"last patch {m:0} min ago" : null,
        }.Where(s => s is not null)),
        a.Subagents.Count > 0 ? "subagents: " + string.Join(" · ", a.Subagents.GroupBy(s => s.State).Select(s => $"{s.Count()} {s.Key}")) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>
    /// Every session that may need a look with its running subagents, then the latest finished sessions up to MaxRows;
    /// the rest as a count.
    /// </summary>
    (List<Line> Lines, int Hidden) VisibleLines()
    {
        var all = _agents ?? [];
        var room = MaxRows - all.Where(a => !a.Done).Sum(a => 1 + Branches(a).Count);
        var lines = new List<Line>();
        var hidden = 0;
        foreach (var a in all)
        {
            if (a.Done && room-- <= 0) { hidden++; continue; }
            lines.Add(new Line(a, Tree.None));
            lines.AddRange(Branches(a));
        }
        return (lines, hidden);
    }

    /// <summary>A session's running subagents, then how many are done. Nothing while none run.</summary>
    static List<Line> Branches(AgentSession a)
    {
        var running = a.Subagents.Where(s => !s.Done).ToList();
        if (running.Count == 0) return [];
        var lines = running.Select(s => new Line(s, Tree.Mid)).ToList();
        if (a.Subagents.Count > running.Count) lines.Add(new Line(null, Tree.Mid, $"+{a.Subagents.Count - running.Count} done"));
        lines[^1] = lines[^1] with { Tree = Tree.End };
        return lines;
    }

    int Lines
    {
        get
        {
            if (_agents is null) return _error is null ? 1 : 2;
            if (_agents.Count == 0) return 1;
            var (lines, hidden) = VisibleLines();
            return lines.Count + (hidden > 0 ? 1 : 0);
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
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

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
            var state = _updated is null ? "Starting…" : "Watcher not running";
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
            Label(g, "No sessions running", small, Theme.Muted, new Rectangle(left, y, right - left, rowH));
            y += rowH;
        }
        else
        {
            var (lines, hidden) = VisibleLines();
            foreach (var line in lines)
            {
                DrawLine(g, line, small, mono, left, right, y);
                if (line.Agent is { } a) _rows.Add((Rectangle.FromLTRB(0, y, Width, y + rowH), a));
                y += rowH;
            }
            if (hidden > 0)
            {
                Label(g, $"+{hidden} more done", small, Theme.Muted, Rectangle.FromLTRB(left + Px(TagW), y, right, y + rowH));
                y += rowH;
            }
        }

        y += Px(4);
        using (var line = new Pen(Theme.Track, 1))
            g.DrawLine(line, left, y, right, y);
        _footer = new Rectangle(0, y, Width, Height - y);
        var footer = Rectangle.FromLTRB(left, y, right, y + Px(FootH));
        if (_updated is { } u)
            Label(g, $"{u:HH:mm} · click to refresh", small, Theme.Muted, footer);
        if (_agents is not null)
        {
            var overview = "overview ↗";
            var w = TextRenderer.MeasureText(g, overview, small, Size.Empty, TextFormatFlags.NoPadding).Width;
            _overview = Rectangle.FromLTRB(right - w, y, right, y + Px(FootH));
            Label(g, overview, small, Theme.Muted, footer, right: true);
        }
    }

    void DrawLine(Graphics g, Line line, Font small, Font mono, int left, int right, int y)
    {
        var rowH = Px(RowH);
        var x = left;
        if (line.Tree != Tree.None) DrawTree(g, line.Tree, x, y);
        else if (Icons.TryGetValue(line.Agent!.Tool, out var icon))
            g.DrawImage(icon, new Rectangle(x, y + (rowH - Px(IconW)) / 2, Px(IconW), Px(IconW)));
        else Label(g, line.Agent.Tool[..Math.Min(2, line.Agent.Tool.Length)], mono, Theme.Muted, new Rectangle(x, y, Px(TagW), rowH));
        x += Px(TagW);

        if (line.Agent is not { } a)
        {
            Label(g, line.Note ?? "", small, Theme.Muted, Rectangle.FromLTRB(x, y, right, y + rowH));
            return;
        }

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
        Label(g, a.Summary is { Length: > 0 } said ? said : a.Title, small, Theme.Muted, Rectangle.FromLTRB(x, y, tokensX - Px(4), y + rowH));
        Label(g, Tokens(a.TokensPerHour), mono, Theme.Muted, new Rectangle(tokensX, y, Px(TokensW), rowH), right: true);
    }

    /// <summary>The line from a session's icon down to its subagents, with a branch to this one; it stops here on the last.</summary>
    void DrawTree(Graphics g, Tree tree, int x, int y)
    {
        var rowH = Px(RowH);
        float cx = x + Px(IconW) / 2f, mid = y + rowH / 2f;
        using var pen = new Pen(TreeLine, Math.Max(1, Px(1)));
        g.DrawLine(pen, cx, y - (rowH - Px(IconW)) / 2f, cx, tree == Tree.End ? mid : y + rowH);
        g.DrawLine(pen, cx, mid, x + Px(TagW) - Px(5), mid);
    }

    static (string Text, Color Color) Chip(AgentSession a) => a.Circles ? ("circles", Theme.Hot) : (a.State, a.State switch
    {
        "waiting" => Theme.Warn,
        "busy" => Theme.Ok,
        "dead" => Theme.Hot,
        _ => Theme.Muted,
    });

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
