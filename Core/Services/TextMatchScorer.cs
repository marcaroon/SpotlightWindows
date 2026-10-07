namespace SpotlightWindows.Core.Services;

/// <summary>
/// Deterministic text matching and scoring engine.
/// Supports exact match, prefix match, token/word match, abbreviation match, and fuzzy match.
/// All comparisons are case-insensitive.
///
/// Score ranges (higher is better):
///   1000  — exact match
///   800   — prefix match (query is a prefix of the name)
///   600   — all query tokens match name tokens (word match)
///   500   — abbreviation match (query chars match first letters of tokens)
///   300   — substring/contains match
///   100   — fuzzy match (characters appear in order)
///   0     — no match
/// </summary>
public static class TextMatchScorer
{
    // Scoring constants — documented for tuning in later phases
    private const double ScoreExactMatch = 1000.0;
    private const double ScorePrefixMatch = 800.0;
    private const double ScoreTokenMatch = 600.0;
    private const double ScoreAbbreviationMatch = 500.0;
    private const double ScoreContainsMatch = 300.0;
    private const double ScoreFuzzyMatch = 100.0;
    private const double ScoreNoMatch = 0.0;

    /// <summary>
    /// Scores how well a query matches a given name.
    /// Returns 0 for no match, higher values for better matches.
    /// </summary>
    /// <param name="query">The user's search query (will be lowercased).</param>
    /// <param name="nameLower">The pre-lowercased name to match against.</param>
    /// <param name="nameTokens">The pre-computed lowercase tokens of the name.</param>
    public static double Score(string query, string nameLower, string[] nameTokens)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(nameLower))
            return ScoreNoMatch;

        var queryLower = query.ToLowerInvariant().Trim();

        if (queryLower.Length == 0)
            return ScoreNoMatch;

        // 1. Exact match
        if (nameLower == queryLower)
            return ScoreExactMatch;

        // 2. Prefix match — name starts with query
        if (nameLower.StartsWith(queryLower, StringComparison.Ordinal))
        {
            // Bonus for shorter names (tighter match)
            double lengthRatio = (double)queryLower.Length / nameLower.Length;
            return ScorePrefixMatch + (lengthRatio * 100.0);
        }

        // 3. Token match — every query token matches the start of some name token
        var queryTokens = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length > 1 && AllQueryTokensMatchNameTokens(queryTokens, nameTokens))
        {
            double matchRatio = (double)queryTokens.Length / nameTokens.Length;
            return ScoreTokenMatch + (matchRatio * 100.0);
        }

        // 4. Abbreviation match — e.g., "vsc" matches "Visual Studio Code"
        if (queryTokens.Length == 1 && IsAbbreviationMatch(queryLower, nameTokens))
        {
            double lengthRatio = (double)queryLower.Length / nameTokens.Length;
            return ScoreAbbreviationMatch + (Math.Min(lengthRatio, 1.0) * 50.0);
        }

        // 5. Contains match — query is a substring of the name
        if (nameLower.Contains(queryLower, StringComparison.Ordinal))
        {
            double lengthRatio = (double)queryLower.Length / nameLower.Length;
            return ScoreContainsMatch + (lengthRatio * 100.0);
        }

        // 6. Single query token matching a name token prefix
        if (queryTokens.Length == 1 && AnyNameTokenStartsWith(queryLower, nameTokens))
        {
            return ScoreContainsMatch;
        }

        // 7. Fuzzy match — characters of query appear in order in the name
        double fuzzyScore = FuzzyMatch(queryLower, nameLower);
        if (fuzzyScore > 0)
        {
            return ScoreFuzzyMatch + fuzzyScore;
        }

        return ScoreNoMatch;
    }

    /// <summary>
    /// Checks if every query token matches the beginning of at least one name token.
    /// Each name token can only be used once.
    /// </summary>
    private static bool AllQueryTokensMatchNameTokens(string[] queryTokens, string[] nameTokens)
    {
        var used = new bool[nameTokens.Length];

        foreach (var qt in queryTokens)
        {
            bool found = false;
            for (int i = 0; i < nameTokens.Length; i++)
            {
                if (!used[i] && nameTokens[i].StartsWith(qt, StringComparison.Ordinal))
                {
                    used[i] = true;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the query characters match the first letter of each name token.
    /// E.g., "vsc" → ["visual", "studio", "code"] ✓
    /// </summary>
    private static bool IsAbbreviationMatch(string queryLower, string[] nameTokens)
    {
        if (nameTokens.Length == 0 || queryLower.Length == 0)
            return false;

        int tokenIndex = 0;
        for (int i = 0; i < queryLower.Length; i++)
        {
            bool matched = false;
            while (tokenIndex < nameTokens.Length)
            {
                if (nameTokens[tokenIndex].Length > 0 && nameTokens[tokenIndex][0] == queryLower[i])
                {
                    tokenIndex++;
                    matched = true;
                    break;
                }
                tokenIndex++;
            }
            if (!matched) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if any name token starts with the given query.
    /// </summary>
    private static bool AnyNameTokenStartsWith(string queryLower, string[] nameTokens)
    {
        foreach (var token in nameTokens)
        {
            if (token.StartsWith(queryLower, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fuzzy match: checks if all characters in the query appear in order in the name.
    /// Returns a score proportional to the ratio of matched characters,
    /// with bonuses for consecutive matches.
    /// </summary>
    private static double FuzzyMatch(string queryLower, string nameLower)
    {
        int qi = 0;
        int consecutiveBonus = 0;
        int totalBonus = 0;

        for (int ni = 0; ni < nameLower.Length && qi < queryLower.Length; ni++)
        {
            if (nameLower[ni] == queryLower[qi])
            {
                qi++;
                consecutiveBonus++;
                totalBonus += consecutiveBonus; // Reward consecutive character matches
            }
            else
            {
                consecutiveBonus = 0;
            }
        }

        if (qi < queryLower.Length)
            return 0; // Not all query characters found

        // Score based on match coverage and consecutive bonus
        double coverage = (double)queryLower.Length / nameLower.Length;
        return (coverage * 50.0) + (totalBonus * 2.0);
    }
}
