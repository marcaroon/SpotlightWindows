using System.Runtime.InteropServices;
using System.Windows.Interop;
using SpotlightWindows.Core.Services;

namespace SpotlightWindows.Infrastructure.Windows;

/// <summary>
/// Manages a global hotkey using the native Windows RegisterHotKey/UnregisterHotKey API.
/// Only one hotkey is registered at a time. The service handles registration,
/// unregistration, and failure logging.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // Win32 hotkey constants
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x9000; // Arbitrary unique ID for our hotkey

    // Win32 modifier flags
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000; // Prevents repeated WM_HOTKEY while key is held

    // Virtual key codes
    private const uint VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly LoggingService _log = LoggingService.Instance;
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private bool _isRegistered;
    private bool _disposed;

    /// <summary>
    /// Raised when the registered global hotkey is pressed.
    /// </summary>
    public event Action? HotkeyPressed;

    /// <summary>
    /// Registers Alt+Space as a global hotkey. Must be called after the window has a valid handle.
    /// </summary>
    /// <param name="window">The WPF window to receive hotkey messages.</param>
    /// <returns>True if registration succeeded, false otherwise.</returns>
    public bool Register(System.Windows.Window window)
    {
        if (_isRegistered)
        {
            _log.Warning("Hotkey already registered, skipping duplicate registration");
            return true;
        }

        var interopHelper = new WindowInteropHelper(window);

        // EnsureHandle creates the Win32 window handle even if the WPF window isn't shown yet
        _windowHandle = interopHelper.EnsureHandle();

        if (_windowHandle == IntPtr.Zero)
        {
            _log.Error("Failed to obtain window handle for hotkey registration");
            return false;
        }

        // Hook into the Win32 message loop to receive WM_HOTKEY
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);

        // Register Alt+Space with MOD_NOREPEAT to avoid repeated firing while held
        bool success = RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, VK_SPACE);

        if (success)
        {
            _isRegistered = true;
            _log.Info("Global hotkey registered: Alt+Space");
        }
        else
        {
            int errorCode = Marshal.GetLastWin32Error();
            _log.Error($"Failed to register global hotkey Alt+Space (Win32 error: {errorCode}). " +
                       "Another application may have registered this hotkey.");
        }

        return success;
    }

    /// <summary>
    /// Unregisters the global hotkey and cleans up the message hook.
    /// </summary>
    public void Unregister()
    {
        if (!_isRegistered) return;

        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _log.Info("Global hotkey unregistered");
        }

        _hwndSource?.RemoveHook(WndProc);
        _isRegistered = false;
    }

    /// <summary>
    /// Win32 message handler — listens for WM_HOTKEY messages.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
