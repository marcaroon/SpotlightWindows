using SpotlightWindows.Core.Services;

namespace SpotlightWindows.CoreTests;

/// <summary>
/// Fixed app/file catalogue scored with the REAL TextMatchScorer / FileMatchScorer and the
/// production weights, to see how apps and files interleave. Used by tests and by `--table`.
/// </summary>
public static class RankingScenarios
{
    public const double AppWeight = 1.0;
    public const double FileWeight = 0.5; // keep in sync with App.xaml.cs

    public static readonly string[] Apps =
    {
        "Google Chrome", "Visual Studio Code", "Visual Studio 2022", "Windows Terminal", "Notepad",
        "Microsoft Edge", "Microsoft Word", "Microsoft Excel", "Calculator", "Command Prompt",
        "Task Manager", "File Explorer", "Spotify", "Slack",
    };

    public static readonly (string Name, bool IsFolder)[] Files =
    {
        ("chrome.exe", false), ("Chrome Downloads", true), ("code.txt", false), ("code", true),
        ("terminal-notes.txt", false), ("notepad.exe", false), ("report.docx", false), ("report", true),
        ("Annual Report 2025.pdf", false), ("invoice.xlsx", false), ("word-list.txt", false),
        ("excel-tips.md", false), ("setup.exe", false), ("readme.md", false),
    };

    public static List<(string Title, string Kind, double Score)> Rank(string query)
    {
        var q = query.ToLowerInvariant();
        var all = new List<(string, string, double)>();

        foreach (var app in Apps)
        {
            var lower = app.ToLowerInvariant();
            var tokens = lower.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var s = TextMatchScorer.Score(q, lower, tokens);
            if (s >= 50) all.Add((app, "app", s * AppWeight));
        }

        foreach (var (name, isFolder) in Files)
        {
            // The real provider only receives rows that CONTAINS(...) matched; mimic that
            if (!name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            all.Add((name, isFolder ? "folder" : "file", FileMatchScorer.Score(name, q, isFolder) * FileWeight));
        }

        return all.OrderByDescending(r => r.Item3).ThenBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void PrintTable()
    {
        foreach (var query in new[] { "chrome", "code", "term", "note", "word", "excel", "report", "invoice", "set" })
        {
            Console.WriteLine($"\n  query: \"{query}\"");
            foreach (var (title, kind, score) in Rank(query).Take(5))
                Console.WriteLine($"    {score,7:F1}  {kind,-6} {title}");
        }
    }
}
