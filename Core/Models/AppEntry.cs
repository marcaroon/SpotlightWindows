namespace SpotlightWindows.Core.Models;

/// <summary>
/// Represents a cached application entry discovered from Start Menu shortcuts
/// or other Windows application registration sources.
/// </summary>
public sealed class AppEntry
{
    /// <summary>Display name of the application (from shortcut name).</summary>
    public required string Name { get; init; }

    /// <summary>Full path to the executable or shortcut (.lnk).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Full path to the shortcut file (.lnk), if discovered via shortcut.</summary>
    public string? ShortcutPath { get; init; }

    /// <summary>
    /// Lowercased, pre-computed name tokens for fast search matching.
    /// E.g., "Visual Studio Code" → ["visual", "studio", "code"]
    /// </summary>
    public string[] NameTokens { get; init; } = [];

    /// <summary>Lowercased full name for fast matching.</summary>
    public string NameLower { get; init; } = string.Empty;
}
