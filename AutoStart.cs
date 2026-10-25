using System;
using System.IO;
using Microsoft.Win32;

namespace DeskTidy
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DeskTidy";

        private static string ExePath
        {
            get
            {
                try
                {
                    string p = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
                catch { }

                // 单文件发布时 Assembly.Location 是空串，直接用进程目录兜底
                return Path.Combine(AppContext.BaseDirectory, "DeskTidy.exe");
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                var v = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(v);
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;

                if (enabled) key.SetValue(ValueName, "\"" + ExePath + "\"");
                else key.DeleteValue(ValueName, false);
            }
            catch { }
        }
    }
}
