using System;

namespace DeskTidy
{
    /// <summary>
    /// Keeps the panel at the bottom of the normal window stack while staying visible
    /// on the desktop after "show desktop".
    ///
    /// The shell does not hide windows for "show desktop" - it raises the wallpaper
    /// window (WorkerW) above everything, which buries ordinary windows behind it.
    /// Giving our window that shell window as its owner makes Windows keep us directly
    /// above it, so we ride up with the desktop and stay visible.
    ///
    /// Sinking to the bottom of the z-order then puts us right on top of the desktop
    /// and below every other window.
    /// </summary>
    internal static class DesktopMode
    {
        /// <summary>Own the window to the desktop and sink it to the bottom.</summary>
        public static void Attach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            IntPtr host = FindDesktopHost();
            if (host != IntPtr.Zero)
                Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, host);

            Sink(hwnd);
        }

        /// <summary>Push the window below every normal window, directly above the desktop.</summary>
        public static void Sink(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            try
            {
                if ((Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0)
                {
                    Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }

                Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch
            {
            }
        }

        /// <summary>Bring the window above every normal window while the user is working with it.</summary>
        public static void Raise(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            try
            {
                if (Native.IsIconic(hwnd))
                    Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);

                if (IsAtTop(hwnd)) return;

                // HWND_TOP is ignored by this window, so go through TOPMOST and back.
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch
            {
            }
        }

        /// <summary>
        /// The shell window that hosts the desktop icons. This is the one the shell
        /// raises for "show desktop", so it is the owner we need.
        /// </summary>
        private static IntPtr FindDesktopHost()
        {
            IntPtr host = IntPtr.Zero;

            Native.EnumWindows((h, _) =>
            {
                var cls = ClassNameOf(h);
                if (cls != "Progman" && cls != "WorkerW") return true;
                if (Native.FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                    return true;

                host = h;
                return false;
            }, IntPtr.Zero);

            if (host != IntPtr.Zero) return host;

            host = Native.FindWindowW("Progman", null);
            return host != IntPtr.Zero ? host : Native.GetShellWindow();
        }

        private static bool IsAtTop(IntPtr target)
        {
            bool atTop = false;

            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h)) return true;

                int ex = Native.GetWindowLongW(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOPMOST) != 0) return true;

                atTop = h == target;
                return false;
            }, IntPtr.Zero);

            return atTop;
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            Native.GetClassName(hwnd, sb, 256);
            return sb.ToString();
        }
    }
}
