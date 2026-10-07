using SpotlightWindows.Core.Models;

namespace SpotlightWindows.Core.Interfaces;

/// <summary>
/// A search provider that can report results incrementally, so fast sources
/// (applications) are shown immediately instead of waiting for slow ones (files).
/// </summary>
public interface IProgressiveSearchProvider : ISearchProvider
{
    /// <summary>
    /// Runs the search and invokes <paramref name="onUpdate"/> each time more results are available.
    /// The final callback has <see cref="SearchSnapshot.IsComplete"/> = true.
    /// The callback runs on the caller's synchronization context (the UI thread when called from WPF).
    /// Throws <see cref="OperationCanceledException"/> if cancelled; no callback is invoked after cancellation.
    /// </summary>
    Task SearchProgressiveAsync(string query, Action<SearchSnapshot> onUpdate, CancellationToken cancellationToken);
}
