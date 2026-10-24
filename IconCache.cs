using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DeskTidy
{
    /// <summary>
    /// 通过 IShellItemImageFactory 拿到高分辨率图标（比 ExtractAssociatedIcon 清晰得多），
    /// 结果按路径缓存在内存里，避免重复 COM 调用。
    /// </summary>
    internal static class IconCache
    {
        private static readonly Dictionary<string, BitmapSource> _cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private const int RequestSize = 128;

        public static BitmapSource Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            lock (_cache)
            {
                if (_cache.TryGetValue(path, out var cached)) return cached;
            }

            BitmapSource result = Extract(path);

            lock (_cache)
            {
                _cache[path] = result;
            }
            return result;
        }

        public static void Invalidate(string path)
        {
            lock (_cache) { _cache.Remove(path); }
        }

        private static BitmapSource Extract(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return null;

                var iid = typeof(Native.IShellItemImageFactory).GUID;
                int hr = Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
                if (hr != 0 || factory == null) return null;

                try
                {
                    var size = new Native.SIZE { cx = RequestSize, cy = RequestSize };
                    int flags = Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK;
                    hr = factory.GetImage(size, flags, out IntPtr hbm);
                    if (hr != 0 || hbm == IntPtr.Zero) return null;

                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hbm, IntPtr.Zero, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally
                    {
                        Native.DeleteObject(hbm);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
