using System.Diagnostics;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Central search orchestrator. Replaces the Phase 3 CompositeSearchProvider.
///
///   - Runs every provider in parallel, always off the caller's thread (Task.Run), so a provider
///     that does synchronous work can never freeze the UI.
///   - Enforces a per-provider timeout by *abandoning* a slow provider (it is also asked to cancel),
///     so cooperative cancellation is not required for the timeout to work.
///   - Publishes a merged snapshot every time a provider finishes (progressive results).
///   - Applies a per-provider score multiplier (the "weight") exactly once per result.
///   - Deduplicates by <see cref="SearchResult.Id"/>; on a clash the higher-scored result wins,
///     with registration order as the tie-breaker, so the outcome does not depend on arrival order.
///
/// Provider contract: SearchAsync must return NEW SearchResult instances on every call,
/// because the orchestrator multiplies <see cref="SearchResult.Score"/> in place.
/// </summary>
public sealed class SearchOrchestrator : IProgressiveSearchProvider
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
    /// Registers a provider with a score multiplier and optional timeout.
    /// 1.0 = unchanged, 0.75 = deprioritize by 25%, 1.5 = boost by 50%.
    /// Call during startup, before searches begin.
    /// </summary>
    public void RegisterProvider(ISearchProvider provider, double scoreMultiplier = 1.0, int? timeoutMs = null)
    {
        if (!provider.IsEnabled) return;

        var timeout = timeoutMs ?? DefaultTimeoutMs;
        _registrations.Add(new ProviderRegistration(_registrations.Count, provider, scoreMultiplier, timeout));
        _log.Info($"Registered search provider: {provider.Name} (weight: {scoreMultiplier:F2}, timeout: {timeout}ms)");
    }

    /// <summary>Non-progressive convenience: waits for all providers and returns the final merged list.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        IReadOnlyList<SearchResult> final = Array.Empty<SearchResult>();
        await SearchProgressiveAsync(query, snapshot => final = snapshot.Results, cancellationToken);
        return final;
    }

    public async Task SearchProgressiveAsync(string query, Action<SearchSnapshot> onUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);

        var registrations = _registrations.ToArray();
        if (string.IsNullOrWhiteSpace(query) || registrations.Length == 0)
        {
            Publish(onUpdate, new SearchSnapshot(Array.Empty<SearchResult>(), IsComplete: true));
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var pending = registrations.Select(r => RunProviderAsync(r, query, cancellationToken)).ToList();
        var collected = new List<ProviderResult>(registrations.Length);
        var latest = (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>();

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending); // resumes on caller's context (UI thread)
            pending.Remove(finished);

            // Never publish for a search that has been superseded
            cancellationToken.ThrowIfCancellationRequested();

            collected.Add(await finished);
            latest = MergeResults(collected);
            Publish(onUpdate, new SearchSnapshot(latest, IsComplete: pending.Count == 0));
        }

        stopwatch.Stop();
        _log.Info($"Search \"{query}\" completed: {latest.Count} results in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Runs one provider off-thread with a hard timeout. Never throws: failure, timeout and
    /// cancellation all yield an empty result set (the caller checks its own token afterwards).
    /// </summary>
    private async Task<ProviderResult> RunProviderAsync(ProviderRegistration reg, string query, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IReadOnlyList<SearchResult>>? work = null;

        try
        {
            var token = providerCts.Token;

            // Task.Run guarantees the provider's synchronous prefix never executes on the caller's thread
            work = Task.Run(() => reg.Provider.SearchAsync(query, token), CancellationToken.None);

            var raw = await work.WaitAsync(TimeSpan.FromMilliseconds(reg.TimeoutMs), ct).ConfigureAwait(false);
            sw.Stop();

            var results = raw ?? Array.Empty<SearchResult>();
            foreach (var result in results)
                result.Score *= reg.ScoreMultiplier; // applied once, here, and nowhere else

            _log.Info($"  Provider '{reg.Provider.Name}': {results.Count} results in {sw.ElapsedMilliseconds}ms");
            return new ProviderResult(reg.Order, results);
        }
        catch (TimeoutException)
        {
            providerCts.Cancel(); // ask it to stop; we have already stopped waiting
            _log.Error($"  Provider '{reg.Provider.Name}': TIMEOUT after {sw.ElapsedMilliseconds}ms (limit: {reg.TimeoutMs}ms)");
            return ProviderResult.Empty(reg.Order);
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Empty(reg.Order);
        }
        catch (Exception ex)
        {
            _log.Error($"  Provider '{reg.Provider.Name}': FAILED after {sw.ElapsedMilliseconds}ms", ex);
            return ProviderResult.Empty(reg.Order);
        }
        finally
        {
            // An abandoned provider may still be running: observe its fault and only dispose
            // the linked CTS once it has really finished.
            if (work is null)
            {
                providerCts.Dispose();
            }
            else
            {
                _ = work.ContinueWith(
                    t => { _ = t.Exception; providerCts.Dispose(); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>Merges everything collected so far: dedupe by Id, sort, trim to MaxResults.</summary>
    private List<SearchResult> MergeResults(List<ProviderResult> collected)
    {
        var best = new Dictionary<string, (SearchResult Result, int Order)>(StringComparer.OrdinalIgnoreCase);

        foreach (var providerResult in collected)
        {
            foreach (var result in providerResult.Results)
            {
                if (best.TryGetValue(result.Id, out var existing))
                {
                    bool better = result.Score > existing.Result.Score
                        || (result.Score == existing.Result.Score && providerResult.Order < existing.Order);
                    if (better) best[result.Id] = (result, providerResult.Order);
                }
                else
                {
                    best[result.Id] = (result, providerResult.Order);
                }
            }
        }

        var merged = best.Values.Select(v => v.Result).ToList();
        merged.Sort(CompareResults);

        if (merged.Count > MaxResults)
            merged.RemoveRange(MaxResults, merged.Count - MaxResults);

        return merged;
    }

    private static int CompareResults(SearchResult a, SearchResult b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;

        int byTitle = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Id, b.Id);
    }

    private void Publish(Action<SearchSnapshot> onUpdate, SearchSnapshot snapshot)
    {
        try
        {
            onUpdate(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error("Search update callback failed", ex);
        }
    }

    private sealed record ProviderRegistration(int Order, ISearchProvider Provider, double ScoreMultiplier, int TimeoutMs);

    private sealed record ProviderResult(int Order, IReadOnlyList<SearchResult> Results)
    {
        public static ProviderResult Empty(int order) => new(order, Array.Empty<SearchResult>());
    }
}
