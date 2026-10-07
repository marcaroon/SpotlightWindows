namespace SpotlightWindows.Core.Models;

/// <summary>
/// A point-in-time view of an in-flight search, published each time another
/// provider finishes. <see cref="Results"/> is always the full merged, ranked
/// list so far (not a delta), so a consumer can simply replace what it shows.
/// </summary>
/// <param name="Results">Merged, deduplicated, ranked results from all providers that have finished so far.</param>
/// <param name="IsComplete">True once every provider has finished, failed, or timed out.</param>
public sealed record SearchSnapshot(IReadOnlyList<SearchResult> Results, bool IsComplete);
