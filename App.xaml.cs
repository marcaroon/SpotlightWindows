using System.Windows;
using SpotlightWindows.Core.Services;
using SpotlightWindows.Infrastructure.Windows;
using SpotlightWindows.UI.Views;

namespace SpotlightWindows;

/// <summary>
/// Application entry point. Manages:
/// - Global hotkey registration (Alt+Space)
/// - Launcher window lifecycle (single persistent instance)
/// - Unhandled exception capture
/// - Clean shutdown
/// </summary>
public partial class App : Application
{
    private readonly LoggingService _log = LoggingService.Instance;
    private HotkeyService? _hotkeyService;
    private MainWindow? _launcherWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log.Info("Application starting");

        // Capture unhandled exceptions to prevent silent crashes
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Create the single launcher window instance (hidden initially)
        _launcherWindow = new MainWindow();
        _launcherWindow.ExitRequested += OnExitRequested;

        // Initialize and inject search providers (Phase 2 + Phase 3)
        var appSearchProvider = new ApplicationSearchProvider();
        var fileSearchProvider = new FileSearchProvider();
        var compositeProvider = new CompositeSearchProvider(appSearchProvider, fileSearchProvider);
        _launcherWindow.SetSearchProvider(compositeProvider);

        // Register the global hotkey
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;

        bool registered = _hotkeyService.Register(_launcherWindow);
        if (!registered)
        {
            MessageBox.Show(
                "Could not register the global hotkey Alt+Space.\n\n" +
                "Another application may already be using this shortcut.\n" +
                "The application will continue, but the hotkey won't work.",
                "Spotlight — Hotkey Registration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _log.Info("Application startup complete — waiting for hotkey");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.Info("Application exiting");

        _hotkeyService?.Dispose();
        _hotkeyService = null;

        base.OnExit(e);
    }

    /// <summary>
    /// Toggles the launcher window when Alt+Space is pressed.
    /// </summary>
    private void OnHotkeyPressed()
    {
        if (_launcherWindow is null) return;

        if (_launcherWindow.IsVisible)
        {
            _launcherWindow.HideWindow();
        }
        else
        {
            _launcherWindow.ShowWindow();
        }
    }

    /// <summary>
    /// Handles the exit request from the launcher window (e.g., Ctrl+Q).
    /// </summary>
    private void OnExitRequested()
    {
        _log.Info("Exit requested by user");
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true; // Prevent crash — log and continue
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _log.Error("Unhandled domain exception", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log.Error("Unobserved task exception", e.Exception);
        e.SetObserved(); // Prevent crash
    }
}
