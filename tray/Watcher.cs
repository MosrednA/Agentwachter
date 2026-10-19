using System.ComponentModel;
using System.Diagnostics;

namespace Agentwachter;

/// <summary>
/// Starts agentwachter.mjs (in this app's folder or one above it, or in a watcher folder there) with node from PATH when nothing
/// answers on the port, and stops it again on exit. A watcher started some other way is left alone.
/// </summary>
sealed class Watcher : IDisposable
{
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    Process? _process;
    DateTime _lastStart;

    /// <summary>Why the watcher isn't answering, as far as we know: its first stderr line, or why it could not start.</summary>
    public string? LastError { get; private set; }

    /// <summary>A watcher started by this app is still running.</summary>
    public bool Running
    {
        get
        {
            if (_process is null) return false;
            if (!_process.HasExited) return true;
            _process.WaitForExit(); // returns at once, after the last stderr lines have arrived
            return false;
        }
    }

    /// <returns>True when it was just started; false when it already runs here, was tried recently, or cannot start.</returns>
    public bool TryStart()
    {
        if (Running || DateTime.Now - _lastStart < RetryAfter) return false;
        _lastStart = DateTime.Now;

        var script = FindScript();
        if (script is null)
        {
            LastError = "agentwachter.mjs not found next to this app";
            return false;
        }
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("node", $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError ??= e.Data; };
            LastError = null;
            process.Start();
            process.BeginErrorReadLine();
            _process?.Dispose();
            _process = process;
            return true;
        }
        catch (Win32Exception ex)
        {
            LastError = "cannot start node: " + ex.Message;
            return false;
        }
    }

    static string? FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var script in new[] { Path.Combine(dir.FullName, "agentwachter.mjs"),
                                           Path.Combine(dir.FullName, "watcher", "agentwachter.mjs") })
                if (File.Exists(script)) return script;
        }
        return null;
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _process?.Dispose();
    }
}
