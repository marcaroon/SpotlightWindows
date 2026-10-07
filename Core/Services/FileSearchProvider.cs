using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;
using SpotlightWindows.Infrastructure.Windows;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Searches files and folders using the Windows Search Index via OLE DB.
/// Does NOT perform full filesystem scans — only queries indexed content.
///
/// Uses the Windows Search SQL dialect:
///   Provider=Search.CollatorDSO;Extended Properties='Application=Windows'
///
/// Returns both files and folders as SearchResult items.
/// </summary>
public sealed class FileSearchProvider : ISearchProvider
{
    public string Name => "Files";
    public bool IsEnabled => true;

    private readonly LoggingService _log = LoggingService.Instance;

    // OLE DB connection string for Windows Search
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties='Application=Windows'";

    // Maximum results to return per query
    private const int MaxResults = 10;

    // Rows pulled from the index before ranking. The SQL ORDER BY is alphabetical, so a pool that is
    // only slightly larger than MaxResults can cut off the best match (e.g. an exact name starting
    // with a late letter) before it is ever scored. Rows are cheap; icons are not (see below).
    private const int CandidatePoolSize = 100;

    /// <summary>
    /// Queries the Windows Search Index for files and folders matching the query.
    /// Runs on a background thread to avoid blocking the UI.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<SearchResult>();

        try
        {
            return await Task.Run(() => QueryWindowsSearch(query, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _log.Error("Windows Search query failed", ex);
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// Executes the OLE DB query, scores every candidate row, keeps the best MaxResults,
    /// and only then builds SearchResults (icon extraction is the expensive step).
    /// </summary>
    private List<SearchResult> QueryWindowsSearch(string query, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();

        // Sanitize query for SQL — escape single quotes, remove double quotes
        var sanitized = query.Replace("'", "''").Replace("\"", "").Trim();
        if (string.IsNullOrEmpty(sanitized))
            return new List<SearchResult>();

        // Windows Search SQL query using CONTAINS for indexed content
        // SCOPE limits to file: protocol (excludes Outlook, OneNote, etc.)
        // Uses canonical property names (locale-independent)
        var sql = $"""
            SELECT TOP {CandidatePoolSize}
                System.ItemName,
                System.ItemPathDisplay,
                System.ItemType
            FROM SystemIndex
            WHERE SCOPE='file:'
              AND CONTAINS(System.ItemName, '"*{sanitized}*"')
            ORDER BY System.ItemName ASC
            """;

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            cancellationToken.ThrowIfCancellationRequested();

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 5; // 5 second timeout

            using var reader = command.ExecuteReader();
            if (reader is null) return new List<SearchResult>();

            var queryLower = sanitized.ToLowerInvariant();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var candidate = ReadCandidate(reader, queryLower);
                    if (candidate is not null)
                        candidates.Add(candidate);
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to read search result row", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Windows Search OLE DB query failed for: {sanitized}", ex);
        }

        // Rank all candidates, keep the best, and only then pay for icons.
        candidates.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        if (candidates.Count > MaxResults)
            candidates.RemoveRange(MaxResults, candidates.Count - MaxResults);

        var results = new List<SearchResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(CreateSearchResult(candidate));
        }

        return results;
    }

    /// <summary>
    /// Reads one row into a lightweight candidate (no icon, no per-row disk access in the common case).
    /// </summary>
    private static Candidate? ReadCandidate(OleDbDataReader reader, string queryLower)
    {
        var itemName = reader["System.ItemName"] as string;
        var itemPath = reader["System.ItemPathDisplay"] as string;
        var itemType = reader["System.ItemType"] as string;

        if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(itemPath))
            return null;

        // Folders are reported as "Directory". A missing ItemType is ambiguous (some folders, but also
        // extension-less files like "LICENSE"), so only that case touches the disk. Previously every
        // empty ItemType was treated as a folder, which mislabelled extension-less files.
        bool isFolder = string.Equals(itemType, "Directory", StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrEmpty(itemType) && Directory.Exists(itemPath));

        return new Candidate(itemName, itemPath, isFolder, FileMatchScorer.Score(itemName, queryLower, isFolder));
    }

    /// <summary>Builds the final SearchResult (including the icon) for a winning candidate.</summary>
    private SearchResult CreateSearchResult(Candidate candidate)
    {
        // Subtitle: parent folder path for files, full path for folders
        var subtitle = candidate.IsFolder
            ? candidate.Path
            : Path.GetDirectoryName(candidate.Path) ?? candidate.Path;

        var path = candidate.Path;

        return new SearchResult
        {
            Id = path,
            Title = candidate.Name,
            Subtitle = subtitle,
            ResultType = candidate.IsFolder ? SearchResultType.Folder : SearchResultType.File,
            Score = candidate.Score,
            Path = path,
            IconSource = IconExtractor.ExtractIcon(path),
            ExecuteAction = () => OpenItem(path),
            RevealAction = () => RevealInExplorer(path)
        };
    }

    private sealed record Candidate(string Name, string Path, bool IsFolder, double Score);

    /// <summary>
    /// Opens a file or folder using the default associated application.
    /// </summary>
    private void OpenItem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            _log.Info($"Opened: {path}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to open: {path}", ex);
        }
    }

    /// <summary>
    /// Reveals a file or folder in Windows Explorer.
    /// For files: selects the file in its parent folder.
    /// For folders: opens the folder.
    /// </summary>
    private void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            _log.Info($"Revealed in Explorer: {path}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to reveal in Explorer: {path}", ex);
        }
    }
}
