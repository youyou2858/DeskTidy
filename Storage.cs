using System;
using System.IO;
using System.Text.Json;

namespace DeskTidy
{
    internal static class Storage
    {
        public static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskTidy");

        public static readonly string DataFile = Path.Combine(Root, "data.json");
        public static readonly string LibraryRoot = Path.Combine(Root, "Library");
        public static readonly string DragTemp = Path.Combine(Path.GetTempPath(), "DeskTidyDrag");

        public static string DesktopDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static string PublicDesktopDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LibraryRoot);
        }

        public static string ZoneLibrary(Zone zone)
        {
            string dir = Path.Combine(LibraryRoot, zone.Id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static AppData Load()
        {
            try
            {
                EnsureDirs();
                if (!File.Exists(DataFile)) return new AppData();

                string json = File.ReadAllText(DataFile);
                var data = JsonSerializer.Deserialize<AppData>(json, Options);
                return data ?? new AppData();
            }
            catch
            {
                // 数据损坏时不要炸掉，备份一份再重建
                try
                {
                    if (File.Exists(DataFile))
                        File.Copy(DataFile, DataFile + ".bak", true);
                }
                catch { }
                return new AppData();
            }
        }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void Save(AppData data)
        {
            try
            {
                EnsureDirs();
                string json = JsonSerializer.Serialize(data, Options);
                string tmp = DataFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, DataFile, true);
                File.Delete(tmp);
            }
            catch
            {
                // 静默失败：保存出问题不该影响使用
            }
        }

        /// <summary>是否是需要"搬进 Library"的快捷方式类小文件。</summary>
        public static bool IsShortcutFile(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext == ".lnk" || ext == ".url" || ext == ".website";
        }

        public static bool IsOnDesktop(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(dir)) return false;
                return string.Equals(dir.TrimEnd('\\'), DesktopDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dir.TrimEnd('\\'), PublicDesktopDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void CleanDragTemp()
        {
            try
            {
                if (Directory.Exists(DragTemp))
                    foreach (var f in Directory.GetFiles(DragTemp))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
