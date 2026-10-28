using System;
using System.Threading;
using System.Windows;

namespace DeskTidy
{
    public partial class App : Application
    {
        private static Mutex _mutex;
        public static TrayIcon Tray { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, "DeskTidy_SingleInstance_v1", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 上次运行留下的拖拽临时副本（正常退出时会被延迟清理掉，异常退出可能残留）
            Storage.CleanDragTemp();

            DispatcherUnhandledException += (s, args) =>
            {
                // 桌面小工具不该因为一次异常就整个崩掉
                args.Handled = true;
            };

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
            Tray = new TrayIcon(win);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { Tray?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
