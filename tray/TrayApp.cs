using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Agentwachter;

sealed class TrayApp : ApplicationContext
{
    // The watcher refreshes every 30 s; asking a loopback port that often costs nothing.
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    readonly AgentsFlyout _flyout = new(); // created first: installs the WinForms sync context for awaits
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    readonly Watcher _watcher = new();
    IReadOnlyList<AgentSession>? _agents;
    DateTimeOffset? _updated;
    bool _busy;

    public TrayApp()
    {
        var autostart = new ToolStripMenuItem(L.T("Start with Windows", "Start met Windows"))
        {
            Checked = Autostart.Enabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Enabled = autostart.Checked;

        _tray.ContextMenuStrip = new ContextMenuStrip
        {
            Items =
            {
                new ToolStripMenuItem(L.T("Refresh", "Vernieuwen"), null, (_, _) => _ = RefreshAsync()),
                new ToolStripMenuItem(L.T("Open overview", "Overzicht openen"), null, (_, _) => OpenOverview()),
                new ToolStripSeparator(),
                autostart,
                new ToolStripSeparator(),
                new ToolStripMenuItem(L.T("Quit", "Afsluiten"), null, (_, _) => ExitThread()),
            },
        };
        _tray.Icon = TrayIcon.Render(null);
        _tray.Text = "Agentwachter";
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        _tray.Visible = true;

        _flyout.RefreshRequested += () => _ = RefreshAsync();
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    void ToggleFlyout()
    {
        if (_flyout.Visible) { _flyout.Hide(); return; }
        // The click that deactivated (and hid) the flyout lands here right after; don't reopen.
        if ((DateTime.Now - _flyout.LastHidden).TotalMilliseconds < 300) return;

        _flyout.SetData(_agents, _updated, _watcher.LastError);
        _flyout.ShowNearTray();
        _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var agents = await AgentsSource.ReadAsync();
            // Nothing answers: start the watcher here and give its first pass a few seconds, unless it quits.
            if (agents is null && _watcher.TryStart())
                for (var i = 0; i < 15 && agents is null && _watcher.Running; i++)
                {
                    await Task.Delay(1000);
                    agents = await AgentsSource.ReadAsync();
                }
            _agents = agents;
            _updated = DateTimeOffset.Now;
            UpdateTray();
            _flyout.SetData(_agents, _updated, _watcher.LastError);
        }
        finally
        {
            _busy = false;
        }
    }

    void UpdateTray()
    {
        var old = _tray.Icon;
        _tray.Icon = TrayIcon.Render(_agents);
        old?.Dispose();

        var tip = _agents is null
            ? "Agentwachter: " + L.T("watcher not running", "watcher draait niet")
            : "Agentwachter" + string.Concat(_agents
                .GroupBy(a => a.Circles ? L.T("in circles", "rondjes") : a.State)
                .Select(s => $" · {s.Count()} {s.Key}"));
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    static void OpenOverview()
    {
        try { using var _ = Process.Start(new ProcessStartInfo(AgentsSource.PageUrl) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _flyout.Dispose();
        _watcher.Dispose();
        base.ExitThreadCore();
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Agentwachter";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
