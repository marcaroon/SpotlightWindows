using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotlightWindows.Core.Services;

namespace SpotlightWindows.Infrastructure.Windows;

/// <summary>
/// Extracts application icons from executable files and shortcuts using
/// the Windows Shell API (SHGetFileInfo). Falls back to a generic icon
/// on failure — never throws.
/// </summary>
public static class IconExtractor
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private static readonly LoggingService _log = LoggingService.Instance;

    /// <summary>
    /// Extracts the large icon from the given file path.
    /// Returns null on failure (never throws).
    /// The returned ImageSource is frozen for cross-thread safety.
    /// </summary>
    public static ImageSource? ExtractIcon(string filePath)
    {
        try
        {
            // Files AND folders: SHGetFileInfo returns the shell icon for both. The previous
            // File.Exists-only guard silently gave every folder result a blank icon.
            if (string.IsNullOrEmpty(filePath) || !(File.Exists(filePath) || Directory.Exists(filePath)))
                return null;

            var shinfo = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                filePath, 0, ref shinfo,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON);

            if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // Freeze for cross-thread safety (icons may be extracted on background thread)
                imageSource.Freeze();
                return imageSource;
            }
            finally
            {
                // Always release the icon handle to prevent GDI leaks
                DestroyIcon(shinfo.hIcon);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to extract icon from: {filePath}", ex);
            return null;
        }
    }
}
