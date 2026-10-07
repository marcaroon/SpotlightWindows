using SpotlightWindows.Core.Models;

namespace SpotlightWindows.Core.Interfaces;

/// <summary>
/// Contract for any search provider (applications, files, calculator, etc.).
/// Each provider independently searches its domain and returns scored results.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Display name of this provider (e.g., "Applications", "Files").</summary>
    string Name { get; }

    /// <summary>Whether this provider is currently enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Search for results matching the given query.
    /// Implementations must support cancellation via the token.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}
