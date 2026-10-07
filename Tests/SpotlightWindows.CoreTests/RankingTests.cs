using static SpotlightWindows.CoreTests.TestRunner;

namespace SpotlightWindows.CoreTests;

/// <summary>
/// Intent checks for how apps and files interleave under the production weights.
/// These are regression guards for hand-tuned numbers, not proof of ideal ranking:
/// when Phase 6 usage-history ranking lands, revisit them deliberately rather than loosening them silently.
/// </summary>
public static class RankingTests
{
    private static string Top(string query) => RankingScenarios.Rank(query).First().Title;

    public static void Run()
    {
        Check("\"chrome\": the Google Chrome app outranks chrome.exe", Top("chrome") == "Google Chrome", Top("chrome"));
        Check("\"code\": Visual Studio Code app outranks code.txt", Top("code") == "Visual Studio Code", Top("code"));
        Check("\"term\": Windows Terminal app outranks terminal-notes.txt", Top("term") == "Windows Terminal", Top("term"));
        Check("\"note\": Notepad app outranks notepad.exe", Top("note") == "Notepad", Top("note"));
        Check("\"excel\": Microsoft Excel app outranks excel-tips.md", Top("excel") == "Microsoft Excel", Top("excel"));
        Check("\"report\": no app matches, exact folder 'report' wins", Top("report") == "report", Top("report"));
        Check("\"invoice\": files still surface when no app matches", Top("invoice") == "invoice.xlsx", Top("invoice"));
        Check("\"readme\": files still surface when no app matches", Top("readme") == "readme.md", Top("readme"));
    }
}
