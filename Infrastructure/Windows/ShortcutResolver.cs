using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SpotlightWindows.Core.Services;

namespace SpotlightWindows.Infrastructure.Windows;

/// <summary>
/// Resolves Windows shortcut (.lnk) files to their target executable paths
/// using the COM IShellLink interface. Does not require Shell32 COM interop assembly.
/// </summary>
public static class ShortcutResolver
{
    private static readonly LoggingService _log = LoggingService.Instance;

    /// <summary>
    /// Resolves a .lnk shortcut to its target path.
    /// Returns null if resolution fails.
    /// </summary>
    public static string? ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return null;

            // Create ShellLink COM object
            var shellLink = (IShellLinkW)new ShellLink();
            var persistFile = (IPersistFile)shellLink;

            // Load the shortcut
            persistFile.Load(shortcutPath, 0 /* STGM_READ */);

            // Resolve the target (without UI, with a short timeout)
            shellLink.Resolve(IntPtr.Zero, 0x0001 /* SLR_NO_UI */ | 0x8 /* SLR_NOUPDATE */);

            // Get the target path
            var targetPath = new StringBuilder(260);
            shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 0);

            var result = targetPath.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to resolve shortcut: {shortcutPath}", ex);
            return null;
        }
    }

    // ═══════════════════════════════════════════════
    // COM interop declarations for IShellLink
    // ═══════════════════════════════════════════════

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd, // WIN32_FIND_DATAW* — we pass IntPtr.Zero
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName);

        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath);

        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath);

        void SetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath,
            int iIcon);

        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPathRel,
            uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
