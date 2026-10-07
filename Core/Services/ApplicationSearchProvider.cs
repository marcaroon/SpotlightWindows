using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;
using SpotlightWindows.Core.Services;
using SpotlightWindows.Infrastructure.Windows;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Discovers and searches installed applications by scanning Start Menu shortcuts.
/// Applications are indexed once at startup and cached in memory.
/// Search is fast and synchronous over the cached index.
///
/// Discovery sources:
///   - User Start Menu: %APPDATA%\Microsoft\Windows\Start Menu\Programs
///   - System Start Menu: %PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs
/// </summary>
public sealed class ApplicationSearchProvider : ISearchProvider
{
    public string Name => "Applications";
    public bool IsEnabled => true;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly object _indexLock = new();
    private List<AppEntry>? _appIndex;
    private bool _isIndexing;

    // Maximum results to return per query
    private const int MaxResults = 8;

    // Minimum score to include in results
    private const double MinScore = 50.0;

    /// <summary>
    /// Searches the application index for matches.
    /// Lazily builds the index on first call.
    /// </summary>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        // Ensure index is built
        EnsureIndexBuilt();

        var index = _appIndex;
        if (index is null || index.Count == 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        cancellationToken.ThrowIfCancellationRequested();

        // Score all apps against the query
        var results = new List<SearchResult>();
        foreach (var app in index)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double score = TextMatchScorer.Score(query, app.NameLower, app.NameTokens);
            if (score >= MinScore)
            {
                results.Add(CreateSearchResult(app, score));
            }
        }

        // Sort by score descending, then by name for tie-breaking
        results.Sort((a, b) =>
        {
            int scoreCompare = b.Score.CompareTo(a.Score);
            return scoreCompare != 0 ? scoreCompare : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        // Return top N results
        if (results.Count > MaxResults)
            results.RemoveRange(MaxResults, results.Count - MaxResults);

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    /// <summary>
    /// Builds the application index if not already built.
    /// Thread-safe — only one indexing operation runs at a time.
    /// </summary>
    private void EnsureIndexBuilt()
    {
        if (_appIndex is not null) return;

        lock (_indexLock)
        {
            if (_appIndex is not null) return;
            if (_isIndexing) return;

            _isIndexing = true;
            try
            {
                _appIndex = BuildIndex();
                _log.Info($"Application index built: {_appIndex.Count} applications discovered");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to build application index", ex);
                _appIndex = new List<AppEntry>();
            }
            finally
            {
                _isIndexing = false;
            }
        }
    }

    /// <summary>
    /// Scans Start Menu directories for .lnk files and builds the application index.
    /// </summary>
    private List<AppEntry> BuildIndex()
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // User Start Menu
        var userStartMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");

        // System Start Menu
        var systemStartMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");

        ScanDirectory(userStartMenu, entries);
        ScanDirectory(systemStartMenu, entries);

        return entries.Values.ToList();
    }

    /// <summary>
    /// Scans a directory recursively for .lnk files and adds valid application entries.
    /// </summary>
    private void ScanDirectory(string directory, Dictionary<string, AppEntry> entries)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var lnkFile in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var entry = ProcessShortcut(lnkFile);
                    if (entry is not null && !entries.ContainsKey(entry.NameLower))
                    {
                        entries[entry.NameLower] = entry;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Failed to process shortcut: {lnkFile}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to scan directory: {directory}", ex);
        }
    }

    /// <summary>
    /// Processes a single .lnk file into an AppEntry.
    /// Filters out uninstaller shortcuts and non-executable targets.
    /// </summary>
    private AppEntry? ProcessShortcut(string lnkPath)
    {
        // Get display name from filename (without extension)
        var name = Path.GetFileNameWithoutExtension(lnkPath);
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Filter out common uninstallers and non-app shortcuts
        var nameLower = name.ToLowerInvariant();
        if (nameLower.Contains("uninstall") ||
            nameLower.Contains("readme") ||
            nameLower.Contains("help") ||
            nameLower.Contains("documentation") ||
            nameLower.Contains("release notes") ||
            nameLower.Contains("license"))
        {
            return null;
        }

        // Resolve the shortcut target
        var targetPath = ShortcutResolver.ResolveShortcutTarget(lnkPath);

        // Use the shortcut itself as the executable path if resolution fails
        // (some shortcuts like UWP apps don't resolve to a traditional .exe)
        var executablePath = targetPath ?? lnkPath;

        // Filter out non-executable targets (but keep unresolved shortcuts for UWP apps)
        if (targetPath is not null)
        {
            var ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (ext != ".exe" && ext != ".msc" && ext != ".cmd" && ext != ".bat")
                return null;
        }

        // Pre-compute search tokens
        var tokens = nameLower
            .Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);

        return new AppEntry
        {
            Name = name,
            ExecutablePath = executablePath,
            ShortcutPath = lnkPath,
            NameLower = nameLower,
            NameTokens = tokens
        };
    }

    /// <summary>
    /// Creates a SearchResult from an AppEntry with the given score.
    /// Icon extraction and launch action are attached here.
    /// </summary>
    private SearchResult CreateSearchResult(AppEntry app, double score)
    {
        // Extract icon from the shortcut file (preferred) or executable
        ImageSource? icon = null;
        var iconPath = app.ShortcutPath ?? app.ExecutablePath;
        if (!string.IsNullOrEmpty(iconPath))
        {
            icon = IconExtractor.ExtractIcon(iconPath);
        }

        return new SearchResult
        {
            Id = app.ExecutablePath,
            Title = app.Name,
            Subtitle = "Application",
            ResultType = SearchResultType.Application,
            Score = score,
            Path = app.ExecutablePath,
            IconSource = icon,
            ExecuteAction = () => LaunchApplication(app)
        };
    }

    /// <summary>
    /// Launches an application. Handles failures gracefully.
    /// </summary>
    private void LaunchApplication(AppEntry app)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = app.ShortcutPath ?? app.ExecutablePath,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            _log.Info($"Launched application: {app.Name}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to launch application: {app.Name}", ex);
        }
    }
}
