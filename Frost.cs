using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskTidy
{
    /// <summary>
    /// 磨砂底图：把桌面壁纸按"填充"方式铺满屏幕后做一次重度模糊。
    /// 窗口移动时只需要改 ImageBrush 的 Viewbox（纯纹理坐标运算），
    /// 所以拖拽过程中也不会有任何截屏 / 重算开销。
    /// </summary>
    internal static class Frost
    {
        private static BitmapSource _blurred;
        private static bool _tried;

        public static int ScreenW { get; private set; }
        public static int ScreenH { get; private set; }

        public static BitmapSource GetBlurred()
        {
            if (_tried) return _blurred;
            _tried = true;

            try
            {
                ScreenW = (int)SystemParameters.PrimaryScreenWidth;
                ScreenH = (int)SystemParameters.PrimaryScreenHeight;

                // 用 Win32 拿到物理分辨率，避免 DPI 缩放导致比例错位
                ScreenW = GetSystemMetricsSafe(0);
                ScreenH = GetSystemMetricsSafe(1);

                string path = GetWallpaperPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                var src = LoadImage(path);
                if (src == null) return null;

                BitmapSource cropped = CropToScreen(src, ScreenW, ScreenH);
                BitmapSource small = Downscale(cropped, Math.Max(1, ScreenW / 8), Math.Max(1, ScreenH / 8));
                BitmapSource blurred = BoxBlur(small, 5);
                blurred.Freeze();

                _blurred = blurred;
                return _blurred;
            }
            catch
            {
                return null;
            }
        }

        private static int GetSystemMetricsSafe(int index)
        {
            try { return Native.GetSystemMetrics(index); }
            catch { return 0; }
        }

        private static string GetWallpaperPath()
        {
            try
            {
                var sb = new StringBuilder(1024);
                if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                    return sb.ToString();
            }
            catch { }
            return null;
        }

        private static BitmapSource LoadImage(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>按 UniformToFill 居中裁剪成屏幕比例。</summary>
        private static BitmapSource CropToScreen(BitmapSource src, int sw, int sh)
        {
            if (sw <= 0 || sh <= 0) return src;

            double imgAspect = src.PixelWidth / (double)src.PixelHeight;
            double scrAspect = sw / (double)sh;

            double cw, ch, cx, cy;
            if (imgAspect > scrAspect)
            {
                ch = src.PixelHeight;
                cw = ch * scrAspect;
                cx = (src.PixelWidth - cw) / 2;
                cy = 0;
            }
            else
            {
                cw = src.PixelWidth;
                ch = cw / scrAspect;
                cx = 0;
                cy = (src.PixelHeight - ch) / 2;
            }

            var rect = new Int32Rect(
                Math.Max(0, (int)cx), Math.Max(0, (int)cy),
                Math.Max(1, Math.Min(src.PixelWidth, (int)cw)),
                Math.Max(1, Math.Min(src.PixelHeight, (int)ch)));

            try { return new CroppedBitmap(src, rect); }
            catch { return src; }
        }

        private static BitmapSource Downscale(BitmapSource src, int w, int h)
        {
            try
            {
                var tb = new TransformedBitmap(src, new ScaleTransform(
                    w / (double)src.PixelWidth, h / (double)src.PixelHeight));
                tb.Freeze();
                return tb;
            }
            catch { return src; }
        }

        /// <summary>可分离 box blur，跑两遍已经很接近高斯。</summary>
        private static BitmapSource BoxBlur(BitmapSource src, int radius)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            int stride = w * 4;

            var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            byte[] px = new byte[stride * h];
            converted.CopyPixels(px, stride, 0);

            byte[] tmp = new byte[px.Length];

            for (int pass = 0; pass < 2; pass++)
            {
                BlurHorizontal(px, tmp, w, h, radius);
                BlurVertical(tmp, px, w, h, radius);
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }

        private static void BlurHorizontal(byte[] src, byte[] dst, int w, int h, int r)
        {
            int window = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                int sb = 0, sg = 0, sr = 0, sa = 0;

                for (int i = -r; i <= r; i++)
                {
                    int x = Math.Min(w - 1, Math.Max(0, i));
                    int p = row + x * 4;
                    sb += src[p]; sg += src[p + 1]; sr += src[p + 2]; sa += src[p + 3];
                }

                for (int x = 0; x < w; x++)
                {
                    int p = row + x * 4;
                    dst[p] = (byte)(sb / window);
                    dst[p + 1] = (byte)(sg / window);
                    dst[p + 2] = (byte)(sr / window);
                    dst[p + 3] = (byte)(sa / window);

                    int outX = Math.Min(w - 1, Math.Max(0, x - r));
                    int inX = Math.Min(w - 1, Math.Max(0, x + r + 1));
                    int po = row + outX * 4, pi = row + inX * 4;
                    sb += src[pi] - src[po];
                    sg += src[pi + 1] - src[po + 1];
                    sr += src[pi + 2] - src[po + 2];
                    sa += src[pi + 3] - src[po + 3];
                }
            }
        }

        private static void BlurVertical(byte[] src, byte[] dst, int w, int h, int r)
        {
            int window = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sb = 0, sg = 0, sr = 0, sa = 0;

                for (int i = -r; i <= r; i++)
                {
                    int y = Math.Min(h - 1, Math.Max(0, i));
                    int p = y * w * 4 + col;
                    sb += src[p]; sg += src[p + 1]; sr += src[p + 2]; sa += src[p + 3];
                }

                for (int y = 0; y < h; y++)
                {
                    int p = y * w * 4 + col;
                    dst[p] = (byte)(sb / window);
                    dst[p + 1] = (byte)(sg / window);
                    dst[p + 2] = (byte)(sr / window);
                    dst[p + 3] = (byte)(sa / window);

                    int outY = Math.Min(h - 1, Math.Max(0, y - r));
                    int inY = Math.Min(h - 1, Math.Max(0, y + r + 1));
                    int po = outY * w * 4 + col, pi = inY * w * 4 + col;
                    sb += src[pi] - src[po];
                    sg += src[pi + 1] - src[po + 1];
                    sr += src[pi + 2] - src[po + 2];
                    sa += src[pi + 3] - src[po + 3];
                }
            }
        }
    }
}
