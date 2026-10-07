using SpotlightWindows.CoreTests;

// Optional: `dotnet run -- --table` prints the cross-provider ranking table used for weight tuning.
if (args.Contains("--table"))
{
    RankingScenarios.PrintTable();
    return 0;
}

return TestRunner.RunAll();
