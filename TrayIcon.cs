using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DeskTidy
{
    /// <summary>
    /// 原生托盘图标（Shell_NotifyIcon）。
    /// 相比 WinForms 的 NotifyIcon，省掉整套 WinForms 运行时，内存占用明显更低。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int TrayIconId = 1;
        private const int WM_TRAYICON = Native.WM_APP + 1;

        private readonly MainWindow _win;
        private readonly HwndSource _source;
        private IntPtr _hIcon;
        private bool _added;

        public TrayIcon(MainWindow win)
        {
            _win = win;

            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            if (_source == null) return;

            _source.AddHook(WndProc);
            _hIcon = LoadAppIcon();

            var data = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = TrayIconId,
                uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "DeskTidy · 桌面分区整理"
            };

            _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_TRAYICON) return IntPtr.Zero;

            int evt = lParam.ToInt32() & 0xFFFF;

            if (evt == Native.WM_LBUTTONDBLCLK)
            {
                _win.ShowPanel();
                handled = true;
            }
            else if (evt == Native.WM_RBUTTONUP)
            {
                ShowMenu();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var cm = new ContextMenu();

            var show = new MenuItem { Header = "显示窗口" };
            show.Click += (s, e) => _win.ShowPanel();
            cm.Items.Add(show);

            var snap = new MenuItem { Header = "吸附到顶部居中" };
            snap.Click += (s, e) => { _win.ShowPanel(); _win.SnapToTopPublic(); };
            cm.Items.Add(snap);

            cm.Items.Add(new Separator());

            var autoStart = new MenuItem
            {
                Header = "开机自启",
                IsCheckable = true,
                IsChecked = AutoStart.IsEnabled()
            };
            autoStart.Click += (s, e) => AutoStart.Set(autoStart.IsChecked);
            cm.Items.Add(autoStart);

            cm.Items.Add(new Separator());

            var exit = new MenuItem { Header = "退出 DeskTidy" };
            exit.Click += (s, e) => _win.ExitApp();
            cm.Items.Add(exit);

            // 先让窗口成为前台，否则点菜单外面不会关闭（托盘菜单的经典坑）
            IntPtr hwnd = new WindowInteropHelper(_win).Handle;
            if (hwnd != IntPtr.Zero) Native.SetForegroundWindow(hwnd);

            cm.PlacementTarget = _win;
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }

        private static IntPtr LoadAppIcon()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var large = new IntPtr[1];
                    var small = new IntPtr[1];
                    if (Native.ExtractIconEx(exe, 0, large, small, 1) > 0)
                    {
                        if (small[0] != IntPtr.Zero)
                        {
                            if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]);
                            return small[0];
                        }
                        if (large[0] != IntPtr.Zero) return large[0];
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try
            {
                if (_added && _source != null)
                {
                    var data = new Native.NOTIFYICONDATA
                    {
                        cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                        hWnd = _source.Handle,
                        uID = TrayIconId
                    };
                    Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
                    _added = false;
                }

                if (_source != null) _source.RemoveHook(WndProc);

                if (_hIcon != IntPtr.Zero)
                {
                    Native.DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }
            }
            catch { }
        }
    }
}
