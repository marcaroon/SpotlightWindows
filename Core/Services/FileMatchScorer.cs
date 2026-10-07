namespace SpotlightWindows.Core.Services;

/// <summary>
/// Scores a file/folder name against a query. Extracted from FileSearchProvider so the
/// numbers can be tested and tuned without needing the Windows Search Index.
/// All scores are pre-weight; the orchestrator applies the provider multiplier afterwards.
/// </summary>
public static class FileMatchScorer
{
    public const double ExactName = 700.0;
    public const double PrefixName = 500.0;
    public const double ContainsName = 300.0;
    public const double Default = 200.0;
    public const double FolderBoost = 50.0;

    /// <param name="itemName">File or folder name as indexed (any case).</param>
    /// <param name="queryLower">Already-lowercased query.</param>
    public static double Score(string itemName, string queryLower, bool isFolder)
    {
        var nameLower = itemName.ToLowerInvariant();
        double score;

        if (nameLower == queryLower)
            score = ExactName;
        else if (nameLower.StartsWith(queryLower, StringComparison.Ordinal))
            score = PrefixName + ((double)queryLower.Length / nameLower.Length * 100.0);
        else if (nameLower.Contains(queryLower, StringComparison.Ordinal))
            score = ContainsName + ((double)queryLower.Length / nameLower.Length * 50.0);
        else
            score = Default;

        if (isFolder)
            score += FolderBoost;

        return score;
    }
}
