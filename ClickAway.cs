using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskTidy
{
    /// <summary>
    /// Clears the icon selection when the user clicks anywhere outside the panel.
    ///
    /// Why this is needed: the panel is a child of the desktop icon view (see
    /// <see cref="DesktopMode"/>), so it never becomes the foreground window and WPF's
    /// Deactivated event never fires - clicking the bare desktop used to leave the
    /// selection stuck. A low-level mouse hook is the only reliable way to notice those
    /// clicks.
    ///
    /// The hook is installed only while something actually needs it (an icon is selected,
    /// or the settings popup is open) and removed as soon as it is done. It is a passive
    /// callback, not a timer - no polling, no idle cost.
    /// </summary>
    internal static class ClickAway
    {
        private static IntPtr _hook;
        private static Native.LowLevelMouseProc _proc;
        private static Action _callback;
        private static uint _pid;

        public static bool IsArmed => _hook != IntPtr.Zero;

        /// <summary>Install the hook (or just refresh the callback if already installed).</summary>
        public static void Arm(Action onOutsideClick)
        {
            if (onOutsideClick == null) return;

            _callback = onOutsideClick;
            if (_hook != IntPtr.Zero) return;

            _pid = (uint)Process.GetCurrentProcess().Id;
            _proc = HookProc;

            _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero)
            {
                _proc = null;
                _callback = null;
            }
        }

        public static void Disarm()
        {
            if (_hook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }

            _proc = null;
            _callback = null;
        }

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                    {
                        var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

                        // Any window of our own process counts as "inside" - that covers the
                        // panel itself as well as popups like the settings menu and the
                        // icon context menu, which live in their own top-level HWNDs.
                        if (!BelongsToUs(Native.WindowFromPoint(info.pt)))
                        {
                            var cb = _callback;
                            Disarm();
                            cb?.Invoke();
                        }
                    }
                }
            }
            catch
            {
                // A hook must never throw - it would take the whole mouse input down.
            }

            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static bool BelongsToUs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid == _pid;
        }
    }
}
