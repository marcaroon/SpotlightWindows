namespace SpotlightWindows.CoreTests;

/// <summary>Minimal runner: no framework, non-zero exit code on any failure.</summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;

    public static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  -> " + detail)}"); }
    }

    public static int RunAll()
    {
        Section("TextMatchScorer", ScorerTests.Run);
        Section("FileMatchScorer", ScorerTests.RunFile);
        Section("Cross-provider ranking", RankingTests.Run);
        Section("SearchOrchestrator", OrchestratorTests.Run);

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Section(string title, Action body)
    {
        Console.WriteLine($"\n[{title}]");
        try { body(); }
        catch (Exception ex) { _failed++; Console.WriteLine($"  FAIL  {title} threw {ex.GetType().Name}: {ex.Message}"); }
    }
}
