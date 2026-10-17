using System.Globalization;

namespace Agentwachter;

/// <summary>
/// Two-language string table: Dutch when the Windows display language is Dutch, English otherwise.
/// Override with the AGENTWACHTER_LANG environment variable ("nl" or "en").
/// </summary>
static class L
{
    static readonly bool Dutch =
        (Environment.GetEnvironmentVariable("AGENTWACHTER_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        .StartsWith("nl", StringComparison.OrdinalIgnoreCase);

    public static string T(string en, string nl) => Dutch ? nl : en;
}
