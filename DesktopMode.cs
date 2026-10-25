using System;

namespace DeskTidy
{
    /// <summary>
    /// Turns the panel into a child of the desktop wallpaper window, the way Rainmeter's
    /// "on desktop" mode works.
    ///
    /// Why not an owned window: giving the desktop window as *owner* does keep the panel
    /// visible after "show desktop", but it also breaks the shell's show-desktop toggle -
    /// the second Win+D no longer restores the other windows. A child window is part of
    /// the desktop's own window group, so the shell never sees it as a top-level window
    /// and the toggle keeps working.
    ///
    /// Result: the panel sits at the very bottom of the window stack, rides along when the
    /// desktop is raised, and disappears behind normal windows.
    /// </summary>
    internal static class DesktopMode
    {
        public static void Attach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            IntPtr host = FindDesktopHost();
            if (host == IntPtr.Zero) return;

            try
            {
                Native.ShowWindow(hwnd, Native.SW_HIDE);

                int style = Native.GetWindowLongW(hwnd, Native.GWL_STYLE);
                style = (style & ~Native.WS_POPUP) | Native.WS_CHILD;
                Native.SetWindowLong(hwnd, Native.GWL_STYLE, style);

                Native.SetParent(hwnd, host);

                Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
            }
            catch
            {
            }
        }

        /// <summary>
        /// The desktop icon view itself (SHELLDLL_DefView), not the window that currently
        /// owns it. "Show desktop" moves DefView into a freshly raised WorkerW, so a window
        /// parented to Progman gets buried; a child of DefView travels with it and stays
        /// visible either way.
        /// </summary>
        private static IntPtr FindDesktopHost()
        {
            IntPtr host = IntPtr.Zero;

            Native.EnumWindows((h, _) =>
            {
                var cls = ClassNameOf(h);
                if (cls != "Progman" && cls != "WorkerW") return true;

                IntPtr defView = Native.FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView == IntPtr.Zero) return true;

                host = defView;
                return false;
            }, IntPtr.Zero);

            if (host != IntPtr.Zero) return host;

            IntPtr progman = Native.FindWindowW("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;

            return Native.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            Native.GetClassName(hwnd, sb, 256);
            return sb.ToString();
        }
    }
}
