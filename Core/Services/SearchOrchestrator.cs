using System.Diagnostics;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Central search orchestrator that manages multiple search providers,
/// runs them in parallel with per-provider timeouts, applies configurable
/// score multipliers, deduplicates results, and returns a unified ranked list.
///
/// This replaces the Phase 3 CompositeSearchProvider with:
///   - Configurable per-provider weight multipliers
///   - Per-provider timeout enforcement
///   - Result deduplication by Id
///   - Search timing diagnostics
///   - Progressive result merging
/// </summary>
public sealed class SearchOrchestrator : ISearchProvider
{
    public string Name => "Orchestrator";
    public bool IsEnabled => true;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly List<ProviderRegistration> _registrations = new();

    /// <summary>Maximum total results to return across all providers.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>Default timeout per provider in milliseconds.</summary>
    public int DefaultTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Registers a search provider with a score multiplier and optional timeout.
    ///
    /// The multiplier adjusts how that provider's raw scores compare to others:
    ///   1.0 = no adjustment (default)
    ///   1.5 = boost scores by 50% (prioritize this provider)
    ///   0.5 = reduce scores by 50% (deprioritize this provider)
    /// </summary>
    public void RegisterProvider(ISearchProvider provider, double scoreMultiplier = 1.0, int? timeoutMs = null)
    {
        if (!provider.IsEnabled) return;

        _registrations.Add(new ProviderRegistration
        {
            Provider = provider,
            ScoreMultiplier = scoreMultiplier,
            TimeoutMs = timeoutMs ?? DefaultTimeoutMs
        });

        _log.Info($"Registered search provider: {provider.Name} (weight: {scoreMultiplier:F1}, timeout: {timeoutMs ?? DefaultTimeoutMs}ms)");
    }

    /// <summary>
    /// Runs all registered providers in parallel, applies score multipliers,
    /// deduplicates, and returns a unified ranked list.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || _registrations.Count == 0)
            return Array.Empty<SearchResult>();

        var overallStopwatch = Stopwatch.StartNew();

        // Launch all providers in parallel with individual timeouts
        var tasks = _registrations.Select(reg =>
            SearchWithTimeout(reg, query, cancellationToken));

        var allResultSets = await Task.WhenAll(tasks);

        cancellationToken.ThrowIfCancellationRequested();

        // Merge, deduplicate, sort, and limit
        var merged = MergeResults(allResultSets);

        overallStopwatch.Stop();
        _log.Info($"Search for \"{query}\" completed: {merged.Count} results in {overallStopwatch.ElapsedMilliseconds}ms");

        return merged;
    }

    /// <summary>
    /// Runs a single provider with a timeout. If the provider exceeds its
    /// timeout, the result is discarded (returns empty).
    /// </summary>
    private async Task<ProviderResult> SearchWithTimeout(
        ProviderRegistration reg, string query, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Create a linked token that also enforces the per-provider timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(reg.TimeoutMs);

            var results = await reg.Provider.SearchAsync(query, timeoutCts.Token);

            sw.Stop();
            _log.Info($"  Provider '{reg.Provider.Name}': {results.Count} results in {sw.ElapsedMilliseconds}ms");

            return new ProviderResult
            {
                Results = results,
                ScoreMultiplier = reg.ScoreMultiplier,
                ProviderName = reg.Provider.Name
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-provider timeout (not the overall cancellation)
            sw.Stop();
            _log.Error($"  Provider '{reg.Provider.Name}': TIMEOUT after {sw.ElapsedMilliseconds}ms (limit: {reg.TimeoutMs}ms)");
            return ProviderResult.Empty(reg.Provider.Name);
        }
        catch (OperationCanceledException)
        {
            // Overall cancellation — propagate
            return ProviderResult.Empty(reg.Provider.Name);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error($"  Provider '{reg.Provider.Name}': FAILED after {sw.ElapsedMilliseconds}ms", ex);
            return ProviderResult.Empty(reg.Provider.Name);
        }
    }

    /// <summary>
    /// Merges results from all providers, applies score multipliers,
    /// deduplicates by Id, sorts by score, and limits to MaxResults.
    /// </summary>
    private List<SearchResult> MergeResults(ProviderResult[] providerResults)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SearchResult>();

        // Flatten all results and apply multipliers
        foreach (var pr in providerResults)
        {
            foreach (var result in pr.Results)
            {
                // Apply score multiplier
                result.Score *= pr.ScoreMultiplier;

                // Deduplicate by Id (first occurrence wins — higher multiplier providers
                // should be registered first for priority)
                if (seen.Add(result.Id))
                {
                    merged.Add(result);
                }
            }
        }

        // Sort by score descending, then alphabetically for tie-breaking
        merged.Sort((a, b) =>
        {
            int scoreCompare = b.Score.CompareTo(a.Score);
            return scoreCompare != 0
                ? scoreCompare
                : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        // Limit to MaxResults
        if (merged.Count > MaxResults)
            merged.RemoveRange(MaxResults, merged.Count - MaxResults);

        return merged;
    }

    // ═══════════════════════════════════════════════
    // Internal types
    // ═══════════════════════════════════════════════

    private sealed class ProviderRegistration
    {
        public required ISearchProvider Provider { get; init; }
        public double ScoreMultiplier { get; init; } = 1.0;
        public int TimeoutMs { get; init; } = 3000;
    }

    private sealed class ProviderResult
    {
        public IReadOnlyList<SearchResult> Results { get; init; } = Array.Empty<SearchResult>();
        public double ScoreMultiplier { get; init; } = 1.0;
        public string ProviderName { get; init; } = "";

        public static ProviderResult Empty(string providerName) => new()
        {
            ProviderName = providerName,
            Results = Array.Empty<SearchResult>()
        };
    }
}
