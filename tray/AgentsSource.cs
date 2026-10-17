using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentwachter;

/// <param name="Tool">"claude" or "codex".</param>
/// <param name="State">The watcher's state: "bezig", "wacht op jou", "stil", "klaar" or "dood".</param>
/// <param name="Link">The Claude desktop app's id for the session ("local_…"), when it runs there.</param>
/// <param name="Circles">Busy, mostly repeating itself and touching no new files.</param>
sealed record AgentSession(
    string Tool, string Project, string Title, string State, string Summary, string? Link,
    double TokensPerHour, double ToolsPerHour, double ErrorRate, double RepeatRatio, double? MinsSincePatch, bool Circles)
{
    public bool Done => State == "klaar";
}

/// <summary>Running Claude Code and Codex sessions, from the watcher's JSON on 127.0.0.1.</summary>
static class AgentsSource
{
    public static readonly int Port =
        int.TryParse(Environment.GetEnvironmentVariable("AGENTWACHTER_PORT"), out var port) ? port : 17321;
    public static readonly string PageUrl = $"http://127.0.0.1:{Port}/";

    // Loopback only: either it answers at once or it is not there.
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <returns>Null when the watcher does not answer, or is still starting.</returns>
    public static async Task<IReadOnlyList<AgentSession>?> ReadAsync()
    {
        try
        {
            if (JsonNode.Parse(await Http.GetStringAsync(PageUrl + "agents"))?["sessions"] is not JsonArray sessions) return null;
            return sessions.OfType<JsonObject>().Select(s => new AgentSession(
                Str(s, "tool"), Str(s, "project"), Str(s, "title"), Str(s, "state"), Str(s, "summary"),
                Str(s, "link") is { Length: > 0 } link ? link : null,
                Num(s, "tokensPerHour") ?? 0, Num(s, "toolsPerHour") ?? 0, Num(s, "errorRate") ?? 0, Num(s, "repeatRatio") ?? 0,
                Num(s, "minsSincePatch"),
                s["circles"] is JsonValue c && c.TryGetValue<bool>(out var b) && b)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    static string Str(JsonObject o, string name) => o[name]?.ToString() ?? "";
    static double? Num(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
