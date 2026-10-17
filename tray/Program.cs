namespace Agentwachter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        switch (args)
        {
            // Dev aid: render the flyout with live data to a PNG.
            case ["--snapshot", var path]:
                ApplicationConfiguration.Initialize();
                var agents = AgentsSource.ReadAsync().GetAwaiter().GetResult(); // before the form: it installs the sync context
                using (var flyout = new AgentsFlyout())
                    flyout.RenderTo(agents, path);
                return;
        }

        using var single = new Mutex(true, @"Local\AgentwachterTray", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
