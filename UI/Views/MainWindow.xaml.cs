using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;
using SpotlightWindows.Core.Services;
using SpotlightWindows.Infrastructure.Windows;

namespace SpotlightWindows.UI.Views;

/// <summary>
/// Code-behind for the main launcher window.
/// Manages show/hide lifecycle, async search with debounce,
/// result navigation, and application launching.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly ObservableCollection<SearchResult> _results = new();

    // Search infrastructure
    private ISearchProvider? _searchProvider;
    private CancellationTokenSource? _searchCts;
    private System.Windows.Threading.DispatcherTimer? _debounceTimer;

    // Debounce delay in milliseconds
    private const int DebounceDelayMs = 150;

    /// <summary>
    /// Raised when the user requests to exit the entire application (Ctrl+Q).
    /// </summary>
    public event Action? ExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _log.Info("MainWindow initialized");

        ResultsListBox.ItemsSource = _results;

        // Set up debounce timer
        _debounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DebounceDelayMs)
        };
        _debounceTimer.Tick += OnDebounceTimerTick;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
    }

    /// <summary>
    /// Sets the search provider used to find results.
    /// Called by App.xaml.cs after provider initialization.
    /// </summary>
    public void SetSearchProvider(ISearchProvider provider)
    {
        _searchProvider = provider;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus();
        _log.Info("MainWindow loaded");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            HideWindow();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        HideWindow();
    }

    // ═══════════════════════════════════════════════
    // Window show/hide lifecycle
    // ═══════════════════════════════════════════════

    public void ShowWindow()
    {
        // Cancel any pending search
        CancelPendingSearch();

        // Clear state
        SearchTextBox.Clear();
        _results.Clear();
        UpdateVisualState();

        Show();
        WindowPositionService.CenterOnCurrentMonitor(this);
        Activate();
        Keyboard.Focus(SearchTextBox);

        _log.Info("Launcher shown");
    }

    public void HideWindow()
    {
        if (!IsVisible) return;

        CancelPendingSearch();
        Hide();
        _log.Info("Launcher hidden");
    }

    // ═══════════════════════════════════════════════
    // Keyboard handling
    // ═══════════════════════════════════════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ExitRequested?.Invoke();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;

            case Key.Enter:
                if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    // Ctrl+Shift+Enter — reveal in Explorer
                    RevealSelectedResult();
                }
                else
                {
                    ExecuteSelectedResult();
                }
                e.Handled = true;
                break;

            case Key.Down:
                NavigateResults(1);
                e.Handled = true;
                break;

            case Key.Up:
                NavigateResults(-1);
                e.Handled = true;
                break;

            case Key.Tab:
                // Tab navigates down, Shift+Tab navigates up
                NavigateResults(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    // ═══════════════════════════════════════════════
    // Search with debounce
    // ═══════════════════════════════════════════════

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        WatermarkText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;

        // Reset and restart the debounce timer
        _debounceTimer?.Stop();

        if (!hasText)
        {
            CancelPendingSearch();
            _results.Clear();
            UpdateVisualState();
            return;
        }

        _debounceTimer?.Start();
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
        _ = PerformSearchAsync(SearchTextBox.Text);
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _searchProvider is null)
            return;

        // Cancel any previous search
        CancelPendingSearch();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            var results = await _searchProvider.SearchAsync(query.Trim(), cts.Token);

            // Only update UI if this is still the current search
            if (cts.Token.IsCancellationRequested)
                return;

            _results.Clear();
            foreach (var result in results)
            {
                _results.Add(result);
            }

            // Auto-select the first result
            if (_results.Count > 0)
            {
                ResultsListBox.SelectedIndex = 0;
            }

            UpdateVisualState();
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer query cancels this one
        }
        catch (Exception ex)
        {
            _log.Error("Search failed", ex);
            _results.Clear();
            UpdateVisualState();
        }
    }

    private void CancelPendingSearch()
    {
        _debounceTimer?.Stop();

        if (_searchCts is not null)
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
            _searchCts = null;
        }
    }

    // ═══════════════════════════════════════════════
    // Result navigation and execution
    // ═══════════════════════════════════════════════

    private void NavigateResults(int direction)
    {
        if (_results.Count == 0) return;

        int current = ResultsListBox.SelectedIndex;
        int next = current + direction;

        if (next < 0) next = _results.Count - 1; // Wrap to bottom
        if (next >= _results.Count) next = 0;     // Wrap to top

        ResultsListBox.SelectedIndex = next;
        ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
    }

    private void ExecuteSelectedResult()
    {
        var selected = ResultsListBox.SelectedItem as SearchResult;
        if (selected?.ExecuteAction is null) return;

        _log.Info($"Executing: {selected.Title} (score: {selected.Score:F0})");

        try
        {
            selected.ExecuteAction();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to execute result: {selected.Title}", ex);
        }

        HideWindow();
    }

    private void RevealSelectedResult()
    {
        var selected = ResultsListBox.SelectedItem as SearchResult;
        if (selected?.RevealAction is null)
        {
            // Fallback: execute normally if no reveal action
            ExecuteSelectedResult();
            return;
        }

        _log.Info($"Revealing: {selected.Title}");

        try
        {
            selected.RevealAction();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to reveal result: {selected.Title}", ex);
        }

        HideWindow();
    }

    private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ensure focus stays on the search box even when selection changes
        if (IsVisible && SearchTextBox.IsLoaded)
        {
            Keyboard.Focus(SearchTextBox);
        }
    }

    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelectedResult();
    }

    // ═══════════════════════════════════════════════
    // Visual state management
    // ═══════════════════════════════════════════════

    private void UpdateVisualState()
    {
        bool hasResults = _results.Count > 0;
        bool hasQuery = !string.IsNullOrEmpty(SearchTextBox?.Text);

        ResultsListBox.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;

        if (!hasQuery)
        {
            EmptyStateIcon.Text = "\uE773"; // Search icon
            EmptyStateText.Text = "Start typing to search...";
        }
        else if (!hasResults)
        {
            EmptyStateIcon.Text = "\uE783"; // Info icon
            EmptyStateText.Text = $"No results for \"{SearchTextBox?.Text}\"";
        }
    }
}
