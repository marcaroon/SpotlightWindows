using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Runs multiple search providers in parallel and merges their results.
/// This is a lightweight bridge for Phase 3 — will be replaced by the
/// full SearchOrchestrator with unified ranking in Phase 4.
///
/// Results are sorted by score descending across all providers.
/// </summary>
public sealed class CompositeSearchProvider : ISearchProvider
{
    public string Name => "Composite";
    public bool IsEnabled => true;

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly LoggingService _log = LoggingService.Instance;
    private const int MaxTotalResults = 10;

    public CompositeSearchProvider(params ISearchProvider[] providers)
    {
        _providers = providers.Where(p => p.IsEnabled).ToList();
    }

    /// <summary>
    /// Runs all enabled providers in parallel and merges their results,
    /// sorted by score descending, limited to MaxTotalResults.
    /// A failing provider does not prevent other providers from returning results.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        // Launch all providers in parallel
        var tasks = _providers.Select(provider =>
            SearchProviderSafe(provider, query, cancellationToken));

        var allResultSets = await Task.WhenAll(tasks);

        cancellationToken.ThrowIfCancellationRequested();

        // Merge and sort by score descending
        var merged = allResultSets
            .SelectMany(r => r)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTotalResults)
            .ToList();

        return merged;
    }

    /// <summary>
    /// Wraps a provider's SearchAsync in a try/catch so one provider failure
    /// doesn't take down the entire search.
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>> SearchProviderSafe(
        ISearchProvider provider, string query, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SearchResult>();
        }
        catch (Exception ex)
        {
            _log.Error($"Search provider '{provider.Name}' failed", ex);
            return Array.Empty<SearchResult>();
        }
    }
}
