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

    // Score constants for file/folder results
    private const double ScoreFileExactName = 700.0;
    private const double ScoreFilePrefixName = 500.0;
    private const double ScoreFileContainsName = 300.0;
    private const double ScoreFileDefault = 200.0;
    private const double ScoreFolderBoost = 50.0; // Slight boost for folders

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
    /// Executes the OLE DB query against the Windows Search Index.
    /// </summary>
    private List<SearchResult> QueryWindowsSearch(string query, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();

        // Sanitize query for SQL — escape single quotes, remove wildcards
        var sanitized = query.Replace("'", "''").Replace("\"", "").Trim();
        if (string.IsNullOrEmpty(sanitized))
            return results;

        // Windows Search SQL query using CONTAINS for indexed content
        // SCOPE limits to file: protocol (excludes Outlook, OneNote, etc.)
        // Uses canonical property names (locale-independent)
        var sql = $"""
            SELECT TOP {MaxResults * 2}
                System.ItemName,
                System.ItemPathDisplay,
                System.ItemUrl,
                System.ItemType,
                System.Kind
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
            if (reader is null) return results;

            var queryLower = sanitized.ToLowerInvariant();

            while (reader.Read() && results.Count < MaxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = ReadSearchResult(reader, queryLower);
                    if (result is not null)
                    {
                        results.Add(result);
                    }
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

        return results;
    }

    /// <summary>
    /// Reads a single result row from the OLE DB reader and converts to SearchResult.
    /// </summary>
    private SearchResult? ReadSearchResult(OleDbDataReader reader, string queryLower)
    {
        var itemName = reader["System.ItemName"] as string;
        var itemPath = reader["System.ItemPathDisplay"] as string;
        var itemUrl = reader["System.ItemUrl"] as string;
        var itemType = reader["System.ItemType"] as string;

        if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(itemPath))
            return null;

        // Determine if this is a folder or file
        bool isFolder = string.IsNullOrEmpty(itemType) || // Folders often have null ItemType
                        string.Equals(itemType, "Directory", StringComparison.OrdinalIgnoreCase) ||
                        Directory.Exists(itemPath);

        var resultType = isFolder ? SearchResultType.Folder : SearchResultType.File;

        // Calculate relevance score
        double score = CalculateScore(itemName, queryLower, isFolder);

        // Build subtitle: parent folder path for files, full path for folders
        string subtitle;
        if (isFolder)
        {
            subtitle = itemPath;
        }
        else
        {
            var parentDir = Path.GetDirectoryName(itemPath);
            subtitle = parentDir ?? itemPath;
        }

        // Extract file/folder icon
        var icon = IconExtractor.ExtractIcon(itemPath);

        return new SearchResult
        {
            Id = itemPath,
            Title = itemName,
            Subtitle = subtitle,
            ResultType = resultType,
            Score = score,
            Path = itemPath,
            IconSource = icon,
            ExecuteAction = () => OpenItem(itemPath),
            RevealAction = () => RevealInExplorer(itemPath)
        };
    }

    /// <summary>
    /// Calculates a relevance score for a file/folder result.
    /// </summary>
    private static double CalculateScore(string itemName, string queryLower, bool isFolder)
    {
        var nameLower = itemName.ToLowerInvariant();
        double score;

        if (nameLower == queryLower)
            score = ScoreFileExactName;
        else if (nameLower.StartsWith(queryLower, StringComparison.Ordinal))
            score = ScoreFilePrefixName + ((double)queryLower.Length / nameLower.Length * 100.0);
        else if (nameLower.Contains(queryLower, StringComparison.Ordinal))
            score = ScoreFileContainsName + ((double)queryLower.Length / nameLower.Length * 50.0);
        else
            score = ScoreFileDefault;

        if (isFolder)
            score += ScoreFolderBoost;

        return score;
    }

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
