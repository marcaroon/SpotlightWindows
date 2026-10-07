using System.Windows.Media;

namespace SpotlightWindows.Core.Models;

/// <summary>
/// Represents a single search result from any provider.
/// This is the common model all search providers emit.
/// </summary>
public class SearchResult
{
    /// <summary>Unique identifier for this result (e.g., file path, app path).</summary>
    public required string Id { get; init; }

    /// <summary>Primary display title (e.g., "Visual Studio Code").</summary>
    public required string Title { get; init; }

    /// <summary>Secondary display text (e.g., "Application", file path).</summary>
    public string? Subtitle { get; init; }

    /// <summary>The type of result.</summary>
    public required SearchResultType ResultType { get; init; }

    /// <summary>Relevance score used for ranking. Higher is better.</summary>
    public double Score { get; set; }

    /// <summary>Path to the executable or file, if applicable.</summary>
    public string? Path { get; init; }

    /// <summary>Icon image source for display in the UI. Null uses a generic icon.</summary>
    public ImageSource? IconSource { get; set; }

    /// <summary>Action to execute when this result is selected (Enter key).</summary>
    public Action? ExecuteAction { get; set; }

    /// <summary>Alternate action to reveal the item in Explorer (Ctrl+Shift+Enter).</summary>
    public Action? RevealAction { get; set; }
}

/// <summary>
/// Categorizes search results for display and action dispatching.
/// </summary>
public enum SearchResultType
{
    Application,
    File,
    Folder,
    Calculator,
    WebSearch,
    SystemAction
}
