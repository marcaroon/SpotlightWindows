using SpotlightWindows.Core.Services;
using static SpotlightWindows.CoreTests.TestRunner;

namespace SpotlightWindows.CoreTests;

public static class ScorerTests
{
    private static double S(string query, string name)
    {
        var lower = name.ToLowerInvariant();
        var tokens = lower.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return TextMatchScorer.Score(query, lower, tokens);
    }

    public static void Run()
    {
        // Tier ordering must be strict: exact > prefix > token > abbreviation > word-start > contains > fuzzy
        double exact = S("notepad", "Notepad");
        double prefix = S("note", "Notepad");
        double token = S("visual code", "Visual Studio Code");
        double abbr = S("vsc", "Visual Studio Code");
        double wordStart = S("chrome", "Google Chrome");
        double contains = S("oogle", "Google Chrome");
        double fuzzy = S("vscode", "Visual Studio Code");

        Check("exact = 1000", exact == 1000);
        Check("tier order exact > prefix", exact > prefix);
        Check("tier order prefix > token", prefix > token, $"{prefix} vs {token}");
        Check("tier order token > abbreviation", token > abbr, $"{token} vs {abbr}");
        Check("tier order abbreviation > word-start", abbr > wordStart, $"{abbr} vs {wordStart}");
        Check("tier order word-start > contains", wordStart > contains, $"{wordStart} vs {contains}");
        Check("tier order contains > fuzzy", contains > fuzzy, $"{contains} vs {fuzzy}");
        Check("fuzzy still matches (vscode -> Visual Studio Code)", fuzzy > 0);
        Check("no match returns 0", S("zzz", "Notepad") == 0);
        Check("empty query returns 0", S("  ", "Notepad") == 0);
        Check("case-insensitive", S("NOTEPAD", "Notepad") == 1000);
        Check("word-start stays below abbreviation range (<500)", wordStart < 500, wordStart.ToString("F1"));
    }

    public static void RunFile()
    {
        Check("exact file name = 700", FileMatchScorer.Score("readme.md", "readme.md", false) == 700);
        Check("prefix > contains", FileMatchScorer.Score("invoice client.xlsx", "inv", false) > FileMatchScorer.Score("my invoice.xlsx", "inv", false));
        Check("contains >= 300", FileMatchScorer.Score("my invoice.xlsx", "inv", false) >= 300);
        Check("no substring = default 200", FileMatchScorer.Score("zzz", "inv", false) == 200);
        Check("folder boost = +50", FileMatchScorer.Score("docs", "docs", true) == FileMatchScorer.Score("docs", "docs", false) + 50);
    }
}
