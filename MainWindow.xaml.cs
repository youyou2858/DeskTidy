using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskTidy
{
    public partial class MainWindow : Window
    {
        // ---------- 常量 ----------
        private const double ShadowPad = 16;        // 窗口内为投影预留的边距
        private const double CollapsedHeight = 58;  // 折叠时面板本身的高度
        private const double SnapGap = 34;          // 吸附后面板与屏幕顶部的距离
        private const double SnapTopThreshold = 100; // 触发吸附的纵向范围
        private const double SnapCenterTolerance = 90;  // 触发吸附的横向范围（相对屏幕中线）
        private const double ItemW = 92;
        private const double ItemH = 98;
        private const double MinPanelWidth = 400;   // 面板最小宽度（不含投影边距）
        private const double MaxPanelWidth = 1080;  // 面板最大宽度，超出则分区条横向滚动
        private const double TopChromeWidth = 2 + 28 + 73 + 4; // 边框 + 拖动手柄区 + 右侧按钮 + 余量
        private const string DragFormat = "DeskTidy.ZoneItemId";

        // ---------- 状态 ----------
        private readonly AppData _data;
        private Zone _activeZone;
        private ZoneItem _dragItem;
        private Point _dragStart;
        private bool _internalDrop;
        private bool _closingForReal;
        private string _renameBackup;

        public MainWindow()
        {
            InitializeComponent();

            _data = Storage.Load();

            ApplyTheme();
            ApplyZoneFontSize();
            RestoreWindow();
            BindZones();

            SourceInitialized += (s, e) =>
            {
                ApplyBackdrop();

                if (PresentationSource.FromVisual(this) is HwndSource src)
                {
                    src.AddHook(WndProc);
                    DesktopMode.Attach(src.Handle);
                }
            };

            Loaded += (s, e) =>
            {
                ApplyBackdrop();
                InitFrost();
                UpdateWindowWidth(false);
                UpdateEmptyState();
            };

            // 外壳圆角裁剪：否则子元素的矩形底色会盖住圆角，底部看起来就是方的
            Shell.SizeChanged += (s, e) => ApplyShellClip();
            Loaded += (s, e) => ApplyShellClip();

            LocationChanged += (s, e) => UpdateFrostViewbox();
            SizeChanged += (s, e) => UpdateFrostViewbox();

            // ScrollViewer 会把 MouseLeftButtonDown 标记为已处理，
            // 这里用 handledEventsToo:true 保证顶栏始终能收到拖动指令
            TopBar.AddHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(TopBar_MouseLeftButtonDown), true);
            TopBar.AddHandler(UIElement.MouseMoveEvent,
                new MouseEventHandler(TopBar_MouseMove), true);
            TopBar.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(TopBar_MouseLeftButtonUp), true);

            SettingsPanel.MouseLeave += (s2, e2) => ScheduleSettingsClose();
            SettingsPopup.Closed += (s2, e2) => UpdateClickAway();
            Closed += (s2, e2) => ClickAway.Disarm();

            // 点桌面或其他程序 → 窗口失焦：收起菜单，并取消图标选中
            Deactivated += (s2, e2) =>
            {
                if (SettingsPopup.IsOpen) SettingsPopup.IsOpen = false;
                ClearSelection();
            };

            // 兜底：真被最小化了就立刻还原
            StateChanged += (s2, e2) =>
            {
                if (WindowState == WindowState.Minimized)
                    Dispatcher.BeginInvoke(new Action(() => WindowState = WindowState.Normal),
                        DispatcherPriority.Send);
            };

            PanelHost.PreviewMouseLeftButtonDown += PanelHost_PreviewMouseLeftButtonDown;
            PanelHost.PreviewMouseMove += PanelHost_PreviewMouseMove;
            PanelHost.PreviewMouseLeftButtonUp += PanelHost_PreviewMouseLeftButtonUp;

            // 分区太多、超出最大宽度时，滚轮可以横向翻看
            ZonesScroll.PreviewMouseWheel += (s2, e2) =>
            {
                if (ZonesScroll.ScrollableWidth <= 0) return;
                ZonesScroll.ScrollToHorizontalOffset(ZonesScroll.HorizontalOffset - e2.Delta);
                e2.Handled = true;
            };

            Closing += OnWindowClosing;
        }

        private const double ShellRadius = 15;

        /// <summary>把外壳裁成圆角，保证底部圆弧和顶部一致。</summary>
        private void ApplyShellClip()
        {
            try
            {
                if (Shell.ActualWidth < 1 || Shell.ActualHeight < 1) return;

                Shell.Clip = new RectangleGeometry(
                    new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight),
                    ShellRadius, ShellRadius);
            }
            catch { }
        }

        // ============================================================
        //  磨砂底图
        // ============================================================

        private ImageBrush _frostBrush;

        private void InitFrost()
        {
            // 壁纸模糊 + Viewbox 裁剪：零截屏开销，跟着窗口位置实时平移
            try
            {
                BitmapSource bmp = Frost.GetBlurred();
                if (bmp == null) return;

                _frostBrush = new ImageBrush(bmp)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect(0, 0, 1, 1),
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                FrostLayer.Background = _frostBrush;
                UpdateFrostViewbox();
            }
            catch { }
        }

        /// <summary>窗口移动/变尺寸时，只改纹理坐标，几乎零开销。</summary>
        private void UpdateFrostViewbox()
        {
            if (_frostBrush == null || Frost.ScreenW <= 0 || Frost.ScreenH <= 0) return;
            try
            {
                Point p = Shell.PointToScreen(new Point(0, 0));
                Point q = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));

                double w = q.X - p.X;
                double h = q.Y - p.Y;
                if (w <= 0 || h <= 0) return;

                _frostBrush.Viewbox = new Rect(
                    p.X / Frost.ScreenW, p.Y / Frost.ScreenH,
                    w / Frost.ScreenW, h / Frost.ScreenH);
            }
            catch { }
        }

        // ============================================================
        //  外观 / 磨砂
        // ============================================================

        private void ApplyTheme()
        {
            bool dark = IsDarkEffective();
            var uri = new Uri(dark
                ? "pack://application:,,,/Theme/Dark.xaml"
                : "pack://application:,,,/Theme/Light.xaml");
            var dict = new ResourceDictionary { Source = uri };

            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Clear();
            merged.Add(dict);
        }

        private void ApplyZoneFontSize()
        {
            double size = _data.Window.ZoneFontSize;
            if (size < 9 || size > 30) size = 15;

            // 旧数据里的字号如果不在档位上，吸附到标准档
            double[] presets = { 12, 15, 18, 21 };
            if (presets.All(p => Math.Abs(p - size) > 0.1)) size = 15;
            _data.Window.ZoneFontSize = size;

            var res = Application.Current.Resources;
            res["ZoneFontSize"] = size;
            res["ZoneFrameHeight"] = Math.Max(36, Math.Round(size + 22));   // 整个分区框的高度
            res["ZonePillHeight"] = Math.Max(30, Math.Round(size + 15));
            res["ZoneLabelMaxWidth"] = Math.Round(130 * size / 15.0);      // 名字可显示的长度同比放大
        }

        private void SetZoneFontSize(double size)
        {
            _data.Window.ZoneFontSize = size;
            ApplyZoneFontSize();
            SavePrefs();

            // 字号变了 → 胶囊宽度变了 → 窗口宽度也要重算
            Dispatcher.BeginInvoke(new Action(() => UpdateWindowWidth()), DispatcherPriority.Loaded);
        }

        private bool IsDarkEffective()
        {
            switch (_data.Window.Theme)
            {
                case 1: return true;
                case 2: return false;
                default: return !IsSystemLight();
            }
        }

        private static bool IsSystemLight()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object v = key?.GetValue("AppsUseLightTheme");
                return v is int i && i == 1;
            }
            catch { return false; }
        }

        private void ApplyBackdrop()
        {
            // 分层窗口下，DWM 的 backdrop / Accent 亚克力都会与 WPF 的 alpha 通道打架
            // （实测会导致整个窗口隐形），所以磨砂改由 Frost（实时截屏 + 模糊）实现。
            //
            // 截屏时用 WDA_EXCLUDEFROMCAPTURE 把自己排除掉，但只在那一瞬间设置，
            // 截完立刻恢复 —— 否则用户用截图/录屏工具时会发现窗口凭空消失。
        }

        // ============================================================
        //  窗口位置 / 吸附
        // ============================================================

        private void RestoreWindow()
        {
            Width = Math.Max(MinPanelWidth, _data.Window.Width) + ShadowPad * 2;
            Height = CollapsedHeight + ShadowPad * 2;
            // 桌面模式下窗口恒定在最下层，置顶选项已移除
            Topmost = false;

            if (_data.Window.Left == null || _data.Window.Top == null)
            {
                SnapToTopCenter(false);
            }
            else
            {
                Left = _data.Window.Left.Value;
                Top = _data.Window.Top.Value;
                if (_data.Window.Snapped) SnapToTopCenter(false);
                else ClampToScreen();
            }
        }

        private Rect CurrentScreenBounds()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                IntPtr monitor = hwnd != IntPtr.Zero
                    ? Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)
                    : IntPtr.Zero;

                if (monitor != IntPtr.Zero)
                {
                    var mi = new Native.MONITORINFO();
                    mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));

                    if (Native.GetMonitorInfo(monitor, ref mi))
                    {
                        var wa = mi.rcWork;
                        var src = PresentationSource.FromVisual(this);
                        if (src?.CompositionTarget != null)
                        {
                            var m = src.CompositionTarget.TransformFromDevice;
                            var tl = m.Transform(new Point(wa.Left, wa.Top));
                            var br = m.Transform(new Point(wa.Right, wa.Bottom));
                            return new Rect(tl, br);
                        }
                    }
                }

                var work = SystemParameters.WorkArea;
                return new Rect(work.Left, work.Top, work.Width, work.Height);
            }
            catch
            {
                var work = SystemParameters.WorkArea;
                return new Rect(work.Left, work.Top, work.Width, work.Height);
            }
        }

        private void SnapToTopCenter(bool animate)
        {
            Rect b = CurrentScreenBounds();
            double targetLeft = Math.Round(b.Left + (b.Width - Width) / 2.0);
            double targetTop = Math.Round(b.Top + SnapGap - ShadowPad);

            if (animate) AnimateMoveTo(targetLeft, targetTop);
            else { Left = targetLeft; Top = targetTop; }

            _data.Window.Snapped = true;
        }

        private void AnimateMoveTo(double left, double top)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(320);

            var ax = new DoubleAnimation(left, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var ay = new DoubleAnimation(top, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };

            ax.Completed += (s, e) => { BeginAnimation(LeftProperty, null); Left = left; };
            ay.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = top; };

            BeginAnimation(LeftProperty, ax);
            BeginAnimation(TopProperty, ay);
        }

        private void ClearMoveAnimations()
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
        }

        /// <summary>
        /// 把窗口约束在屏幕工作区内。
        /// 约束的是"可见面板"而不是窗口矩形 —— 窗口四周有 ShadowPad 的投影留白，
        /// 所以面板可以真正贴到屏幕边沿，而透明阴影溢出去也无所谓。
        /// </summary>
        /// <summary>
        /// 把窗口移动到指定的 DIP 坐标并夹在屏幕内。
        /// 挂到桌面窗口下面之后是子窗口，WPF 的 Left/Top 不再生效，只能走 SetWindowPos。
        /// </summary>
        private void MoveWindowTo(double left, double top)
        {
            Rect b = CurrentScreenBounds();
            double minLeft = b.Left - ShadowPad;
            double maxLeft = b.Right - Width + ShadowPad;
            double minTop = b.Top - ShadowPad;
            double maxTop = b.Bottom - Height + ShadowPad;

            left = maxLeft <= minLeft ? minLeft : Math.Max(minLeft, Math.Min(maxLeft, left));
            top = maxTop <= minTop ? minTop : Math.Max(minTop, Math.Min(maxTop, top));

            var hwnd = new WindowInteropHelper(this).Handle;
            var src = PresentationSource.FromVisual(this);

            if (hwnd != IntPtr.Zero && Native.GetParent(hwnd) != IntPtr.Zero && src?.CompositionTarget != null)
            {
                Point dev = src.CompositionTarget.TransformToDevice.Transform(new Point(left, top));
                Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(dev.X), (int)Math.Round(dev.Y),
                    0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                return;
            }

            Left = left;
            Top = top;
        }

        private void ClampToScreen()
        {
            Rect b = CurrentScreenBounds();

            double minLeft = b.Left - ShadowPad;
            double maxLeft = b.Right - Width + ShadowPad;
            double minTop = b.Top - ShadowPad;
            double maxTop = b.Bottom - Height + ShadowPad;

            Left = maxLeft <= minLeft ? minLeft : Math.Max(minLeft, Math.Min(maxLeft, Left));
            Top = maxTop <= minTop ? minTop : Math.Max(minTop, Math.Min(maxTop, Top));
        }

        private bool _dragging;
        private Point _dragGrab;

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (_activeZone != null) Collapse();
                return;
            }
            if (e.ButtonState != MouseButtonState.Pressed) return;

            if (IsInteractive(e.OriginalSource as DependencyObject)) return;

            // 只有最左边那三个竖点本身（22dip 手柄 + 一点余量）能拖动整个界面
            if (e.GetPosition(TopBar).X > 30) return;

            // 分层窗口无法使用系统 DragMove，这里手动跟随鼠标
            ClearMoveAnimations();
            _dragging = true;
            _dragGrab = e.GetPosition(this);
            TopBar.CaptureMouse();
            e.Handled = true;
        }

        private void TopBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

            try
            {
                // 用光标的绝对屏幕坐标，避免"窗口一动、相对坐标就变"的反馈抖动
                if (!Native.GetCursorPos(out Native.POINT pt)) return;

                Point screenDip = ToDip(new Point(pt.X, pt.Y));
                MoveWindowTo(screenDip.X - _dragGrab.X, screenDip.Y - _dragGrab.Y);
            }
            catch { }
        }

        private void TopBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) EndDrag();
        }

        private void EndDrag()
        {
            _dragging = false;
            try { TopBar.ReleaseMouseCapture(); } catch { }
            OnDragFinished();
        }

        /// <summary>点到分区胶囊 / 按钮 / 输入框时，不应该拖动窗口。</summary>
        private static bool IsInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (d is TextBox) return true;
                if (d is FrameworkElement fe && fe.DataContext is Zone) return true;

                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private Point ToDip(Point devicePoint)
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return devicePoint;
            return src.CompositionTarget.TransformFromDevice.Transform(devicePoint);
        }

        private void OnDragFinished()
        {
            Rect b = CurrentScreenBounds();
            double cx = Left + ActualWidth / 2.0;
            double scx = b.Left + b.Width / 2.0;

            if (Top - b.Top < SnapTopThreshold && Math.Abs(cx - scx) < SnapCenterTolerance)
            {
                SnapToTopCenter(true);
            }
            else
            {
                _data.Window.Snapped = false;
                ClampToScreen();
            }
            SavePrefs();
        }

        // ============================================================
        //  分区
        // ============================================================

        private void BindZones()
        {
            if (_data.Zones.Count == 0)
            {
                _data.Zones.Add(new Zone { Name = "常用" });
                _data.Zones.Add(new Zone { Name = "工作" });
                _data.Zones.Add(new Zone { Name = "娱乐" });
            }
            ZonesHost.ItemsSource = _data.Zones;
            Storage.Save(_data);
        }

        // ---------- 分区拖动排序（三点手柄） ----------

        private Zone _dragZone;
        private UIElement _dragVisual;
        private double? _dragGrabOffset;
        private double[] _slotX;
        private double[] _slotW;
        private double _slotMin;
        private double _slotMax;

        private void ZoneHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var zone = (sender as FrameworkElement)?.DataContext as Zone;
            if (zone == null) return;

            _dragZone = zone;
            _dragVisual = null;
            _dragGrabOffset = null;
            CaptureSlots();

            (sender as FrameworkElement)?.CaptureMouse();

            // 抬到最上层，滑动时不被相邻分区盖住
            var container = ZonesHost.ItemContainerGenerator.ContainerFromItem(zone) as FrameworkElement;
            if (container != null) Panel.SetZIndex(container, 100);
            e.Handled = true;
        }

        private void ZoneHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragZone == null || e.LeftButton != MouseButtonState.Pressed) return;

            var container = ZonesHost.ItemContainerGenerator.ContainerFromItem(_dragZone) as FrameworkElement;
            if (container == null) return;

            if (_dragVisual == null || !IsInside(_dragVisual, container))
            {
                _dragVisual = FirstVisualChild(container);
                _dragGrabOffset = null;
            }

            if (_dragVisual == null) return;

            // 换位动画也会给这个容器加位移，叠加到我们的偏移上就会抖，先清掉
            container.RenderTransform = null;

            Point p = e.GetPosition(ZonesHost);
            double slotX = container.TranslatePoint(new Point(0, 0), ZonesHost).X;

            if (!_dragGrabOffset.HasValue) _dragGrabOffset = p.X - slotX;

            double desired = p.X - _dragGrabOffset.Value;
            if (desired < _slotMin) desired = _slotMin;
            if (desired > _slotMax) desired = _slotMax;

            int current = _data.Zones.IndexOf(_dragZone);
            if (current < 0) return;

            int target = EdgeTarget(current, desired, container.ActualWidth);
            if (target != current)
            {
                MoveZoneAnimated(current, target);
                SaveData();

                container.RenderTransform = null;
                slotX = container.TranslatePoint(new Point(0, 0), ZonesHost).X;
            }

            double offset = desired - slotX;
            if (_dragVisual.RenderTransform is TranslateTransform tt) tt.X = offset;
            else _dragVisual.RenderTransform = new TranslateTransform(offset, 0);
        }

        /// <summary>
        /// 换位判定按「边缘对齐」：向右拖时，胶囊右边缘接近下一个的右边缘就换位；
        /// 向左拖时按左边缘镜像处理。这样分区宽窄不一时换位点也一致。
        /// </summary>
        private int EdgeTarget(int index, double desired, double width)
        {
            if (_slotX == null || index < 0 || index >= _slotX.Length) return index;

            const double gap = 0.2;

            if (index < _slotX.Length - 1)
            {
                double nextRight = _slotX[index + 1] + _slotW[index + 1];
                if (desired + width >= nextRight - _slotW[index + 1] * gap) return index + 1;
            }

            if (index > 0)
            {
                if (desired <= _slotX[index - 1] + _slotW[index - 1] * gap) return index - 1;
            }

            return index;
        }

        /// <summary>拖动开始时采样一次槽位几何，此时还没有任何变换。</summary>
        private void CaptureSlots()
        {
            int count = _data.Zones.Count;
            _slotX = new double[count];
            _slotW = new double[count];

            double min = double.MaxValue;
            double max = double.MinValue;

            for (int i = 0; i < count; i++)
            {
                var cp = ZonesHost.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (cp == null) continue;

                double x = cp.TranslatePoint(new Point(0, 0), ZonesHost).X;
                _slotX[i] = x;
                _slotW[i] = cp.ActualWidth;

                if (x < min) min = x;
                if (x > max) max = x;
            }

            _slotMin = min == double.MaxValue ? 0 : min;
            _slotMax = max == double.MinValue ? 0 : max;
        }

        private static UIElement FirstVisualChild(DependencyObject parent)
        {
            return System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent) == 0
                ? null
                : System.Windows.Media.VisualTreeHelper.GetChild(parent, 0) as UIElement;
        }

        private static bool IsInside(UIElement visual, DependencyObject parent)
        {
            DependencyObject cur = visual;
            while (cur != null)
            {
                var up = System.Windows.Media.VisualTreeHelper.GetParent(cur);
                if (up == null) return false;
                if (up == parent) return true;
                cur = up;
            }
            return false;
        }

        private double GetZoneWidth(int index)
        {
            var cp = ZonesHost.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
            return cp?.ActualWidth > 1 ? cp.ActualWidth : 90;
        }

        private void ZoneHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragZone == null) return;

            try { (sender as FrameworkElement)?.ReleaseMouseCapture(); } catch { }
            _dragZone = null;

            try { if (_dragVisual != null) _dragVisual.RenderTransform = null; } catch { }
            _dragVisual = null;
            _dragGrabOffset = null;
            _slotX = null;
            _slotW = null;

            // 恢复层级 + 清掉残留位移
            for (int i = 0; i < _data.Zones.Count; i++)
            {
                var cp = ZonesHost.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (cp == null) continue;

                Panel.SetZIndex(cp, 0);

                if (cp.RenderTransform is TranslateTransform tt && Math.Abs(tt.X) < 0.5)
                    cp.RenderTransform = null;
            }

            UpdateWindowWidth();
            e.Handled = true;
        }

        /// <summary>
        /// 交换分区顺序时做位置补间。
        /// 关键：起点取「当前视觉位置」（含正在进行的动画位移），而不是布局位置，
        /// 否则连续换位时动画会被硬切，看起来一跳一跳的。
        /// </summary>
        private void MoveZoneAnimated(int oldIndex, int newIndex)
        {
            try
            {
                // 记录每个分区此刻真正"看起来"在哪
                var visualX = new Dictionary<Zone, double>();
                var offsetX = new Dictionary<Zone, double>();

                foreach (Zone z in _data.Zones)
                {
                    var cp = ZonesHost.ItemContainerGenerator.ContainerFromItem(z) as FrameworkElement;
                    if (cp == null) continue;

                    double layout = cp.TranslatePoint(new Point(0, 0), ZonesHost).X;
                    double extra = cp.RenderTransform is TranslateTransform tt ? tt.X : 0;

                    visualX[z] = layout + extra;
                    offsetX[z] = extra;
                }

                _data.Zones.Move(oldIndex, newIndex);
                ZonesHost.UpdateLayout();

                foreach (Zone z in _data.Zones)
                {
                    var cp = ZonesHost.ItemContainerGenerator.ContainerFromItem(z) as FrameworkElement;
                    if (cp == null || !visualX.TryGetValue(z, out double vx)) continue;

                    double newLayout = cp.TranslatePoint(new Point(0, 0), ZonesHost).X;
                    double delta = vx - newLayout;

                    if (Math.Abs(delta) < 0.5)
                    {
                        cp.RenderTransform = null;
                        continue;
                    }

                    var transform = new TranslateTransform(delta, 0);
                    cp.RenderTransform = transform;

                    var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    transform.BeginAnimation(TranslateTransform.XProperty, anim);
                }
            }
            catch
            {
                _data.Zones.Move(oldIndex, newIndex);
            }
        }

        private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var zone = (sender as FrameworkElement)?.DataContext as Zone;
            if (zone == null) return;
            e.Handled = true;
            if (zone.IsEditing) return;
            ToggleZone(zone);
        }

        private void ToggleZone(Zone zone)
        {
            if (zone == null) return;

            if (_activeZone == zone)
            {
                Collapse();
                return;
            }

            if (_activeZone != null) _activeZone.IsSelected = false;
            _activeZone = zone;
            zone.IsSelected = true;

            ItemsHost.ItemsSource = zone.Items;
            zone.NotifyCount();
            UpdateEmptyState();

            AnimateHeight(CollapsedHeight + PanelContentHeight(zone));
            FadePanel(0, 1, 190, 60);
            PanelScroll.ScrollToTop();
        }

        private void Collapse()
        {
            if (_activeZone != null) _activeZone.IsSelected = false;
            _activeZone = null;
            AnimateHeight(CollapsedHeight);
            FadePanel(1, 0, 120, 0);
            HideDropLine();
        }

        /// <summary>
        /// 按分区数量与名字长短自动伸缩窗口宽度：
        /// 分区少就窄，分区多就宽，超过上限则分区条横向滚动。
        /// </summary>
        private void UpdateWindowWidth(bool animate = true)
        {
            try
            {
                if (ZonesHost == null || !IsLoaded || _data.Zones.Count == 0) return;

                // 逐个量出胶囊宽度再累加（比依赖布局测量稳定，不受缓存和拉伸影响）
                double contentW = 0;
                double maxLabelW = Math.Round(130 * _data.Window.ZoneFontSize / 15.0);

                foreach (Zone z in _data.Zones)
                {
                    double textW = Math.Min(MeasureZoneTextWidth(z.Name), maxLabelW);
                    contentW += 32 + textW + 14; // 左右 padding 16 + 右外边距 14
                }

                Rect b = CurrentScreenBounds();
                double maxPanel = Math.Min(MaxPanelWidth, b.Width - ShadowPad * 2 - 120);

                double panelW = TopChromeWidth + contentW;
                panelW = Math.Max(MinPanelWidth, Math.Min(maxPanel, panelW));
                panelW = Math.Round(panelW);

                double target = panelW + ShadowPad * 2;
                if (Math.Abs(Width - target) < 0.5) return;

                _data.Window.Width = panelW;

                bool keepCentered = _data.Window.Snapped;
                double targetLeft = keepCentered
                    ? Math.Round(b.Left + (b.Width - target) / 2.0)
                    : Left;

                // 变宽后右/左边沿不能越界
                double minLeft = b.Left - ShadowPad;
                double maxLeft = b.Right - target + ShadowPad;
                targetLeft = maxLeft <= minLeft
                    ? minLeft
                    : Math.Max(minLeft, Math.Min(maxLeft, targetLeft));

                if (animate) AnimateWidthTo(target, keepCentered ? targetLeft : (double?)null);
                else
                {
                    Width = target;
                    if (keepCentered) Left = targetLeft;
                }

                // 宽度变了 → 每行列数变了 → 展开面板的高度也要重算
                if (_activeZone != null)
                    AnimateHeight(CollapsedHeight + PanelContentHeight(_activeZone, panelW));
            }
            catch { }
        }

        private double MeasureZoneTextWidth(string text)
        {
            try
            {
                var ft = new FormattedText(
                    text ?? string.Empty,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Microsoft YaHei UI"),
                                 FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    _data.Window.ZoneFontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                return ft.WidthIncludingTrailingWhitespace;
            }
            catch { return 26; }
        }

        private void AnimateWidthTo(double targetWidth, double? targetLeft)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(230);

            var aw = new DoubleAnimation(targetWidth, dur)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            aw.Completed += (s2, e2) => { BeginAnimation(WidthProperty, null); Width = targetWidth; };
            BeginAnimation(WidthProperty, aw);

            if (targetLeft.HasValue && Math.Abs(targetLeft.Value - Left) > 0.5)
            {
                double tl = targetLeft.Value;
                var al = new DoubleAnimation(tl, dur)
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.Stop
                };
                al.Completed += (s2, e2) => { BeginAnimation(LeftProperty, null); Left = tl; };
                BeginAnimation(LeftProperty, al);
            }
        }

        private double PanelContentHeight(Zone zone, double? panelWidth = null)
        {
            int count = zone?.Items?.Count ?? 0;
            if (count == 0) return 136;

            double w = panelWidth ?? (Width - ShadowPad * 2);
            double avail = Math.Max(200, w - 2 - 24);
            int cols = Math.Max(1, (int)(avail / ItemW));
            int rows = (int)Math.Ceiling(count / (double)cols);
            rows = Math.Min(rows, 3);
            return rows * ItemH + 30;
        }

        private void AnimateHeight(double panelHeight)
        {
            double target = panelHeight + ShadowPad * 2;
            FitHeightOnScreen(target);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            a.Completed += (s, e) => { BeginAnimation(HeightProperty, null); Height = target; };
            BeginAnimation(HeightProperty, a);
        }

        /// <summary>展开变高时如果底部会溢出屏幕，先把窗口往上挪，保证内容整块可见。</summary>
        private void FitHeightOnScreen(double targetHeight)
        {
            try
            {
                Rect b = CurrentScreenBounds();
                double minTop = b.Top - ShadowPad;
                double maxTop = b.Bottom - targetHeight + ShadowPad;

                if (Top > maxTop)
                    Top = Math.Max(minTop, maxTop);
            }
            catch { }
        }

        private void FadePanel(double from, double to, int ms, int delayMs)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            PanelHost.BeginAnimation(OpacityProperty, a);
        }

        private void UpdateEmptyState()
        {
            bool visible = _activeZone != null && _activeZone.Items.Count == 0;
            EmptyHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddZone_Click(object sender, RoutedEventArgs e)
        {
            var zone = new Zone { Name = "新分区" };
            _data.Zones.Add(zone);
            SaveData();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateWindowWidth(false);
                ToggleZone(zone);
                BeginRename(zone);
            }), DispatcherPriority.Loaded);
        }

        private void BeginRename(Zone zone)
        {
            if (zone == null) return;
            foreach (var z in _data.Zones) z.IsEditing = false;

            _renameBackup = zone.Name;
            zone.IsEditing = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindVisual<TextBox>(ZonesHost, t => t.DataContext == zone);
                if (tb != null) { tb.Focus(); tb.SelectAll(); }
            }), DispatcherPriority.Input);
        }

        private void ZoneEdit_KeyDown(object sender, KeyEventArgs e)
        {
            var tb = sender as TextBox;
            var zone = tb?.DataContext as Zone;
            if (zone == null) return;

            if (e.Key == Key.Enter) { EndRename(zone, true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(zone, false); e.Handled = true; }
        }

        private void ZoneEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            var zone = (sender as FrameworkElement)?.DataContext as Zone;
            if (zone != null) EndRename(zone, true);
        }

        private void EndRename(Zone zone, bool commit)
        {
            if (zone == null || !zone.IsEditing) return;

            if (!commit)
                zone.Name = string.IsNullOrWhiteSpace(_renameBackup) ? "分区" : _renameBackup;

            if (string.IsNullOrWhiteSpace(zone.Name)) zone.Name = "分区";
            zone.IsEditing = false;
            SaveData();

            // 名字长短变了，胶囊宽度也跟着变
            Dispatcher.BeginInvoke(new Action(() => UpdateWindowWidth()), DispatcherPriority.Loaded);
        }

        private void Zone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Zone_Drop(object sender, DragEventArgs e)
        {
            var zone = (sender as FrameworkElement)?.DataContext as Zone;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (zone == null || files == null || files.Length == 0) return;

            e.Handled = true;
            HideDropLine();

            if (_activeZone != zone)
            {
                if (_activeZone != null) _activeZone.IsSelected = false;
                _activeZone = zone;
                zone.IsSelected = true;
                ItemsHost.ItemsSource = zone.Items;
                FadePanel(0, 1, 190, 60);
            }

            ImportFiles(zone, files);
        }

        private void Zone_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var zone = fe?.DataContext as Zone;
            if (zone == null) return;
            e.Handled = true;

            var cm = new ContextMenu();

            var rename = new MenuItem { Header = "重命名分区" };
            rename.Click += (s, a) => BeginRename(zone);
            cm.Items.Add(rename);

            var clear = new MenuItem { Header = "清空分区" };
            clear.Click += (s, a) => ClearZone(zone);
            cm.Items.Add(clear);

            cm.Items.Add(new Separator());

            bool hasItems = zone.Items.Count > 0;
            var del = new MenuItem
            {
                Header = hasItems
                    ? $"删除分区（还有 {zone.Items.Count} 个程序，需先清空）"
                    : "删除分区",
                IsEnabled = !hasItems
            };
            del.Click += (s, a) => DeleteZone(zone);
            cm.Items.Add(del);

            cm.PlacementTarget = fe;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

            // 延迟一拍，否则同一次右键的 MouseUp 会立刻把菜单关掉
            Dispatcher.BeginInvoke(new Action(() => cm.IsOpen = true), DispatcherPriority.Background);
        }

        private void ClearZone(Zone zone)
        {
            if (zone == null || zone.Items.Count == 0) return;

            var confirm = MessageBox.Show(
                $"确定要清空「{zone.Name}」吗？\n\n" +
                $"里面的 {zone.Items.Count} 个程序会被移出这个分区；\n" +
                "从桌面拖进来的快捷方式会自动放回桌面，程序本身不会被删除。",
                "清空分区", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            foreach (var item in zone.Items.ToList())
            {
                if (item.Managed) TryMoveToDesktop(item);
            }
            zone.Items.Clear();
            zone.NotifyCount();

            if (_activeZone == zone)
            {
                UpdateEmptyState();
                AnimateHeight(CollapsedHeight + PanelContentHeight(zone));
            }
            SaveData();
        }

        private void DeleteZone(Zone zone)
        {
            if (zone == null) return;

            // 有内容时先提示，确认后照样能删（内容会被移出，不会丢）
            if (zone.Items.Count > 0)
            {
                var ask = MessageBox.Show(
                    $"「{zone.Name}」里还有 {zone.Items.Count} 个程序。\n\n" +
                    "删除这个分区后，它们会从分区里移出；\n" +
                    "从桌面拖进来的文件会放回桌面，程序本身不会被删除。\n\n" +
                    "确定要删除这个分区吗？",
                    "删除分区", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (ask != MessageBoxResult.OK) return;

                foreach (var item in zone.Items.ToList())
                {
                    if (item.Managed) TryMoveToDesktop(item);
                }
            }

            if (_activeZone == zone) Collapse();
            _data.Zones.Remove(zone);
            SaveData();
            UpdateWindowWidth();
        }

        // ============================================================
        //  图标：打开 / 重命名 / 移除
        // ============================================================

        private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ZoneItem;
            if (item == null) return;

            if (e.ClickCount == 2)
            {
                _dragItem = null;
                e.Handled = true;
                OpenItem(item);
                return;
            }

            // 单击选中（Ctrl 追加）
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!ctrl) ClearSelection();
            item.IsSelected = true;

            _dragItem = item;
            _dragStart = e.GetPosition(this);
            _internalDrop = false;

            UpdateClickAway();
        }

        private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragItem = null;
        }

        // ---------- 选中 / 框选 ----------

        private bool _selecting;
        private Point _selectStart;

        private void PanelHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeZone == null) return;
            if (FindItemSource(e.OriginalSource as DependencyObject) != null) return; // 点在图标上

            _selecting = true;
            _selectStart = e.GetPosition(DropLayer);

            SelectionBox.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionBox, _selectStart.X);
            Canvas.SetTop(SelectionBox, _selectStart.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;

            PanelHost.CaptureMouse();
        }

        private void PanelHost_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_selecting) return;

            Point p = e.GetPosition(DropLayer);
            Canvas.SetLeft(SelectionBox, Math.Min(_selectStart.X, p.X));
            Canvas.SetTop(SelectionBox, Math.Min(_selectStart.Y, p.Y));
            SelectionBox.Width = Math.Abs(p.X - _selectStart.X);
            SelectionBox.Height = Math.Abs(p.Y - _selectStart.Y);
        }

        private void PanelHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_selecting) return;

            _selecting = false;
            SelectionBox.Visibility = Visibility.Collapsed;
            try { PanelHost.ReleaseMouseCapture(); } catch { }

            double w = SelectionBox.Width, h = SelectionBox.Height;
            if (w < 6 && h < 6)
            {
                ClearSelection();
                return;
            }

            SelectInRect(new Rect(
                Canvas.GetLeft(SelectionBox), Canvas.GetTop(SelectionBox), w, h));
            UpdateClickAway();
        }

        private void SelectInRect(Rect rect)
        {
            if (_activeZone == null) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!ctrl) ClearSelection();

            foreach (ZoneItem item in _activeZone.Items)
            {
                var cp = ItemsHost.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (cp == null) continue;

                Point p = cp.TranslatePoint(new Point(0, 0), DropLayer);
                var r = new Rect(p, new Size(cp.ActualWidth, cp.ActualHeight));
                if (rect.IntersectsWith(r)) item.IsSelected = true;
            }
        }

        private void ClearSelection()
        {
            if (_activeZone != null)
                foreach (ZoneItem i in _activeZone.Items) i.IsSelected = false;

            UpdateClickAway();
        }

        // ---------- 点窗口外取消选中 ----------
        //
        // 面板挂在桌面图标视图下面（见 DesktopMode），永远是子窗口、从不成为前台窗口，
        // 所以 WPF 的 Deactivated 不会触发。点桌面空白只能靠低级鼠标钩子感知。
        // 钩子只在「有选中项」或「设置弹层打开」时挂上，用完立刻摘掉，不是轮询。

        private void UpdateClickAway()
        {
            if (SettingsPopup.IsOpen || HasSelection())
            {
                if (!ClickAway.IsArmed) ClickAway.Arm(OnClickOutside);
            }
            else if (ClickAway.IsArmed)
            {
                ClickAway.Disarm();
            }
        }

        private void OnClickOutside()
        {
            if (SettingsPopup.IsOpen) SettingsPopup.IsOpen = false;
            ClearSelection();
        }

        private static ZoneItem FindItemSource(DependencyObject d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is ZoneItem zi) return zi;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ---------- Delete 键 ----------

        private const int WM_KEYDOWN = 0x0100;
        private const int VK_DELETE = 0x2E;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_A = 0x41;
        private const int VK_CONTROL = 0x11;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // ---- 挡住"显示桌面"（Win+D / 点任务栏右下角）----
            // 它通过 SetWindowPos(SWP_HIDEWINDOW) 或 SC_MINIMIZE 把窗口藏起来，
            // 这里把这两个动作吃掉，窗口就留在原地。
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                    bool changed = false;

                    // 1) 不让它隐藏窗口
                    if ((wp.flags & Native.SWP_HIDEWINDOW) != 0)
                    {
                        wp.flags &= ~Native.SWP_HIDEWINDOW;
                        changed = true;
                    }

                    if (changed)
                    {
                        Marshal.StructureToPtr(wp, lParam, false);
                        handled = true;
                    }
                }
                catch { }
                return IntPtr.Zero;
            }

            if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == Native.SC_MINIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // ---- 换壁纸后刷新磨砂底图 ----
            if (msg == Native.WM_SETTINGCHANGE && wParam.ToInt32() == Native.SPI_SETDESKWALLPAPER)
            {
                Frost.Reset();
                InitFrost();
                return IntPtr.Zero;
            }

            if (msg != WM_KEYDOWN) return IntPtr.Zero;

            int key = wParam.ToInt32();

            // 正在输入框里改名时，这些快捷键一律让给输入框
            if (Keyboard.FocusedElement is TextBox) return IntPtr.Zero;

            switch (key)
            {
                case VK_DELETE:
                    HandleDeleteKey();
                    handled = true;
                    break;

                case VK_ESCAPE:
                    // 先收菜单，再取消选中
                    if (SettingsPopup.IsOpen)
                    {
                        SettingsPopup.IsOpen = false;
                        handled = true;
                    }
                    else if (HasSelection())
                    {
                        ClearSelection();
                        handled = true;
                    }
                    break;

                case VK_A:
                    if ((Native.GetKeyState(VK_CONTROL) & 0x8000) != 0 && _activeZone != null)
                    {
                        foreach (ZoneItem it in _activeZone.Items) it.IsSelected = true;
                        UpdateClickAway();
                        handled = true;
                    }
                    break;
            }

            return IntPtr.Zero;
        }

        private bool HasSelection()
        {
            if (_activeZone == null) return false;
            foreach (ZoneItem it in _activeZone.Items)
                if (it.IsSelected) return true;
            return false;
        }

        private void HandleDeleteKey()
        {

            var selected = new List<ZoneItem>();
            if (_activeZone != null)
                foreach (ZoneItem i in _activeZone.Items)
                    if (i.IsSelected) selected.Add(i);

            if (selected.Count > 0)
            {
                DeleteSelectedItems(selected);
                return;
            }

            // 没有选中图标时，若当前分区是空的，Delete 直接删这个分区
            if (_activeZone != null && _activeZone.Items.Count == 0)
                DeleteZone(_activeZone);
        }

        private void DeleteSelectedItems(List<ZoneItem> items)
        {
            var zone = _activeZone;
            if (zone == null || items == null || items.Count == 0) return;

            // 直接删（托管文件进回收站，可还原），不再弹确认
            foreach (ZoneItem item in items)
            {
                if (item.Managed) Native.Recycle(item.Path);
                zone.Items.Remove(item);
            }

            zone.NotifyCount();
            UpdateEmptyState();
            SaveData();
            AnimateHeight(CollapsedHeight + PanelContentHeight(zone));
        }

        private void OpenItem(ZoneItem item)
        {
            if (item == null) return;

            if (!item.Exists)
            {
                var r = MessageBox.Show(
                    $"找不到这个项目：\n{item.Path}\n\n要把它从分区里移除吗？",
                    "DeskTidy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.OK) RemoveItem(item, false);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.Path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败：" + ex.Message, "DeskTidy",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ItemNameEdit_KeyDown(object sender, KeyEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ZoneItem;
            if (item == null) return;

            if (e.Key == Key.Enter)
            {
                FinishItemRename(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                item.DisplayName = ZoneItem.MakeDisplayName(item.Path);
                item.IsEditing = false;
                e.Handled = true;
            }
        }

        private void ItemNameEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ZoneItem;
            if (item != null) FinishItemRename(item);
        }

        private void FinishItemRename(ZoneItem item)
        {
            if (item == null || !item.IsEditing) return;
            if (string.IsNullOrWhiteSpace(item.DisplayName))
                item.DisplayName = ZoneItem.MakeDisplayName(item.Path);
            item.IsEditing = false;
            SaveData();
        }

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

        private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var item = fe?.DataContext as ZoneItem;
            if (item == null) return;
            e.Handled = true;

            // 右键先把它选中，再弹出精简菜单
            ClearSelection();
            item.IsSelected = true;

            if (fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }

        // ---------- 菜单顶部快捷图标 ----------

        private void MenuQuick_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            ContextMenu cm = FindAncestor<ContextMenu>(btn);
            var item = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ZoneItem;
            if (item == null) return;

            if (cm != null) cm.IsOpen = false;

            switch (btn.Tag as string)
            {
                case "cut": ShellMenu.InvokeVerb(Hwnd, item.Path, "cut"); break;
                case "copy": ShellMenu.InvokeVerb(Hwnd, item.Path, "copy"); break;
                case "share": ShellMenu.InvokeVerb(Hwnd, item.Path, "share"); break;
                case "properties": ShellMenu.InvokeVerb(Hwnd, item.Path, "properties"); break;
                case "rename": BeginItemRename(item); break;
                case "delete": DeleteSelectedItems(new List<ZoneItem> { item }); break;
            }
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T))
            {
                var next = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
                if (next == null) break;
                d = next;
            }
            return d as T;
        }

        private void BeginItemRename(ZoneItem item)
        {
            if (item == null) return;
            item.IsEditing = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindVisual<TextBox>(ItemsHost, t => t.DataContext == item);
                if (tb != null) { tb.Focus(); tb.SelectAll(); }
            }), DispatcherPriority.Input);
        }

        // ---------- 精简菜单里的系统命令 ----------

        private void ItemMenu_OpenWith(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item != null) ShellMenu.InvokeVerb(Hwnd, item.Path, "openas");
        }

        private void ItemMenu_Copy(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item != null) ShellMenu.InvokeVerb(Hwnd, item.Path, "copy");
        }

        private void ItemMenu_Cut(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item != null) ShellMenu.InvokeVerb(Hwnd, item.Path, "cut");
        }

        private void ItemMenu_CreateShortcut(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item != null) ShellMenu.InvokeVerb(Hwnd, item.Path, "link");
        }

        private void ItemMenu_Properties(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item != null) ShellMenu.InvokeVerb(Hwnd, item.Path, "properties");
        }

        private void ItemMenu_Open(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item == null) return;

            if (!ShellMenu.InvokeVerb(Hwnd, item.Path, "open"))
                OpenItem(item);
        }

        private void ItemMenu_Rename(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item == null) return;

            item.IsEditing = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindVisual<TextBox>(ItemsHost, t => t.DataContext == item);
                if (tb != null) { tb.Focus(); tb.SelectAll(); }
            }), DispatcherPriority.Input);
        }

        private void ItemMenu_ToDesktop(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item == null) return;

            if (TryMoveToDesktop(item)) RemoveItem(item, false);
        }

        private void ItemMenu_Remove(object sender, RoutedEventArgs e)
        {
            var item = GetMenuTarget(sender);
            if (item == null) return;

            bool managed = item.Managed;
            RemoveItem(item, false);

            if (managed)
            {
                var r = MessageBox.Show(
                    $"「{item.DisplayName}」的文件还留在 DeskTidy 的数据文件夹里。\n\n要把它放回桌面吗？",
                    "已从分区移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) TryMoveToDesktop(item);
            }
        }

        private static ZoneItem GetMenuTarget(object sender)
        {
            var mi = sender as MenuItem;
            if (mi == null) return null;

            var cm = mi.Parent as ContextMenu;
            if (cm == null)
            {
                DependencyObject d = mi;
                while (d != null && !(d is ContextMenu))
                    d = LogicalTreeHelper.GetParent(d);
                cm = d as ContextMenu;
            }
            return (cm?.PlacementTarget as FrameworkElement)?.DataContext as ZoneItem;
        }

        // ============================================================
        //  拖拽
        // ============================================================

        private void Item_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;

            ZoneItem item = _dragItem;
            _dragItem = null;

            string payload = PrepareDragPayload(item);
            if (payload == null) return;

            _internalDrop = false;
            item.IsDragSource = true;

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { payload });
            data.SetData(DragFormat, item.Id);

            var effect = DragDropEffects.None;
            try
            {
                effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch { }
            finally
            {
                item.IsDragSource = false;
                HideDropLine();
                Storage.CleanDragTemp();
            }

            // 拖到窗口外并成功放下 → 视为"搬出分区"
            if (!_internalDrop && effect != DragDropEffects.None)
                RemoveItem(item, true);

            _internalDrop = false;
        }

        /// <summary>
        /// 生成拖拽载荷：临时目录里的一个 .lnk。
        /// 这样拖到桌面时 Explorer 复制的是几 KB 的快捷方式，而不是把 exe 搬走。
        /// </summary>
        private string PrepareDragPayload(ZoneItem item)
        {
            try
            {
                Storage.CleanDragTemp();
                Directory.CreateDirectory(Storage.DragTemp);

                string name = SafeFileName(item.DisplayName);
                string dest = Path.Combine(Storage.DragTemp, name + ".lnk");

                // 文件一律原样复制，保留扩展名和全部属性。
                // 之前只对 .lnk 直接复制、其他类型重建快捷方式，
                // 导致 Steam 的 .url 之类特殊快捷方式被"降级"成普通 .lnk，看起来就是损坏了。
                if (File.Exists(item.Path))
                {
                    string ext = Path.GetExtension(item.Path);
                    if (string.IsNullOrEmpty(ext)) ext = ".lnk";

                    dest = Path.Combine(Storage.DragTemp, name + ext);
                    File.Copy(item.Path, dest, true);
                    return File.Exists(dest) ? dest : null;
                }

                // 文件夹：仍生成快捷方式，避免拖出时复制整棵目录
                if (Directory.Exists(item.Path))
                {
                    if (!Shortcut.Create(item.Path, dest)) return null;
                    return File.Exists(dest) ? dest : null;
                }

                return null;
            }
            catch { return null; }
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "快捷方式";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            string s = sb.ToString().Trim().TrimEnd('.');
            return string.IsNullOrEmpty(s) ? "快捷方式" : s;
        }

        private void Item_DragOver(object sender, DragEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;

            bool internalDrag = e.Data.GetDataPresent(DragFormat);
            bool externalDrag = e.Data.GetDataPresent(DataFormats.FileDrop);

            if (!internalDrag && !externalDrag)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;

            ShowDropLine(fe, e.GetPosition(fe).X < fe.ActualWidth / 2);
        }

        private void Item_DragLeave(object sender, DragEventArgs e)
        {
            // 交给 Panel_DragLeave 统一处理，避免子元素间移动时闪烁
        }

        private void Item_Drop(object sender, DragEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var target = fe?.DataContext as ZoneItem;

            HideDropLine();

            if (e.Data.GetDataPresent(DragFormat))
            {
                _internalDrop = true;
                string id = e.Data.GetData(DragFormat) as string;
                ZoneItem src = FindItem(id);
                if (src == null || target == null || src == target) { e.Handled = true; return; }

                bool insertBefore = fe != null && e.GetPosition(fe).X < fe.ActualWidth / 2;
                MoveItem(src, target, insertBefore);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                ImportFiles(_activeZone, e.Data.GetData(DataFormats.FileDrop) as string[]);
                e.Handled = true;
            }
        }

        private void Panel_DragOver(object sender, DragEventArgs e)
        {
            bool internalDrag = e.Data.GetDataPresent(DragFormat);
            bool externalDrag = e.Data.GetDataPresent(DataFormats.FileDrop);

            if (!internalDrag && !externalDrag)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Panel_DragLeave(object sender, DragEventArgs e)
        {
            Point p = e.GetPosition(PanelHost);
            if (p.X < 0 || p.Y < 0 || p.X > PanelHost.ActualWidth || p.Y > PanelHost.ActualHeight)
                HideDropLine();
        }

        private void Panel_Drop(object sender, DragEventArgs e)
        {
            HideDropLine();

            if (e.Data.GetDataPresent(DragFormat))
            {
                _internalDrop = true;
                string id = e.Data.GetData(DragFormat) as string;
                ZoneItem src = FindItem(id);
                if (src != null) MoveItem(src, null, false);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                ImportFiles(_activeZone, e.Data.GetData(DataFormats.FileDrop) as string[]);
                e.Handled = true;
            }
        }

        private void ShowDropLine(FrameworkElement target, bool leftSide)
        {
            try
            {
                Point pt = target.TranslatePoint(
                    new Point(leftSide ? 1 : target.ActualWidth - 1, 0), DropLayer);

                DropLine.Height = Math.Max(28, target.ActualHeight - 30);
                Canvas.SetLeft(DropLine, pt.X - 1);
                Canvas.SetTop(DropLine, pt.Y + 15);
                DropLine.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void HideDropLine()
        {
            if (DropLine != null) DropLine.Visibility = Visibility.Collapsed;
        }

        private ZoneItem FindItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _activeZone?.Items.FirstOrDefault(i => i.Id == id);
        }

        private void MoveItem(ZoneItem src, ZoneItem target, bool insertBefore)
        {
            var zone = _activeZone;
            if (zone == null || src == null) return;

            int from = zone.Items.IndexOf(src);
            if (from < 0) return;

            zone.Items.RemoveAt(from);

            int to;
            if (target == null)
            {
                to = zone.Items.Count;
            }
            else
            {
                int ti = zone.Items.IndexOf(target);
                to = ti < 0 ? zone.Items.Count : (insertBefore ? ti : ti + 1);
            }

            to = Math.Max(0, Math.Min(to, zone.Items.Count));
            zone.Items.Insert(to, src);
            SaveData();
        }

        private void ImportFiles(Zone targetZone, string[] paths)
        {
            var zone = targetZone ?? _activeZone;
            if (zone == null || paths == null || paths.Length == 0) return;

            int added = 0;

            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string path = raw;
                if (!File.Exists(path) && !Directory.Exists(path)) continue;

                try
                {
                    string full = Path.GetFullPath(path);
                    if (full.StartsWith(Path.GetFullPath(Storage.Root), StringComparison.OrdinalIgnoreCase)
                        && full.IndexOf("Library", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                catch { }

                if (zone.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var item = new ZoneItem
                {
                    SourcePath = path,
                    Path = path,
                    DisplayName = ZoneItem.MakeDisplayName(path)
                };

                // 桌面上的一切（文件、文件夹、快捷方式）都真正搬进资料库，
                // 桌面会真的少掉这一项，而不是只留一个引用
                if (Storage.IsOnDesktop(path))
                {
                    try
                    {
                        string dir = Storage.ZoneLibrary(zone);
                        string fileName = Path.GetFileName(path);
                        string dest = Path.Combine(dir, fileName);

                        if (File.Exists(dest) || Directory.Exists(dest))
                            dest = Path.Combine(dir, Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + fileName);

                        if (Directory.Exists(path)) Directory.Move(path, dest);
                        else File.Move(path, dest);

                        item.Path = dest;
                        item.Managed = true;
                    }
                    catch { }
                }

                zone.Items.Add(item);
                added++;
            }

            if (added == 0) return;

            zone.NotifyCount();
            UpdateEmptyState();
            SaveData();
            AnimateHeight(CollapsedHeight + PanelContentHeight(zone));
        }

        private void RemoveItem(ZoneItem item, bool deleteManagedFile)
        {
            var zone = _activeZone;
            if (zone == null || item == null) return;

            zone.Items.Remove(item);

            if (deleteManagedFile && item.Managed)
            {
                try { if (File.Exists(item.Path)) File.Delete(item.Path); }
                catch { }
            }

            zone.NotifyCount();
            UpdateEmptyState();
            SaveData();
            AnimateHeight(CollapsedHeight + PanelContentHeight(zone));
        }

        private bool TryMoveToDesktop(ZoneItem item)
        {
            if (item == null) return false;

            try
            {
                string desktop = Storage.DesktopDir;

                if (item.Managed && (File.Exists(item.Path) || Directory.Exists(item.Path)))
                {
                    string name = Path.GetFileName(item.Path);
                    string target = Path.Combine(desktop, name);

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        if (Directory.Exists(item.Path))
                            target = Path.Combine(desktop, name + " (" + DateTime.Now.ToString("HHmmss") + ")");
                        else
                            target = Path.Combine(desktop,
                                Path.GetFileNameWithoutExtension(name) + " (" + DateTime.Now.ToString("HHmmss") + ")" +
                                Path.GetExtension(name));
                    }

                    if (Directory.Exists(item.Path)) Directory.Move(item.Path, target);
                    else File.Move(item.Path, target);
                    item.Path = target;
                    item.Managed = false;
                    return true;
                }

                if (!item.Exists) return false;

                string baseName = SafeFileName(item.DisplayName);
                string lnk = Path.Combine(desktop, baseName + ".lnk");
                int n = 2;
                while (File.Exists(lnk))
                    lnk = Path.Combine(desktop, baseName + " (" + n++ + ").lnk");

                return Shortcut.Create(item.Path, lnk);
            }
            catch { return false; }
        }

        // ============================================================
        //  更多设置
        // ============================================================

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPopup.IsOpen)
            {
                SettingsPopup.IsOpen = false;
                return;
            }

            BuildSettingsMenu();
            SettingsPopup.IsOpen = true;
            UpdateClickAway();
        }

        /// <summary>
        /// 用 Popup 自绘设置菜单：点击选项只执行动作、不关闭，
        /// 勾选状态在每次点击后整表重建来同步；鼠标移出面板才收起。
        /// </summary>
        private void BuildSettingsMenu()
        {
            SettingsStack.Children.Clear();

            AddAction("吸附到顶部居中", () => { SnapToTopCenter(true); SavePrefs(); });

            AddSeparator();

            AddCheck("外观跟随系统", _data.Window.Theme == 0, () => SetTheme(0));
            AddCheck("深色磨砂", _data.Window.Theme == 1, () => SetTheme(1));
            AddCheck("浅色磨砂", _data.Window.Theme == 2, () => SetTheme(2));

            AddSeparator();

            double fs = _data.Window.ZoneFontSize;
            AddCheck("分区字号：小（12）", Math.Abs(fs - 12) < 0.1, () => SetZoneFontSize(12));
            AddCheck("分区字号：标准（15）", Math.Abs(fs - 15) < 0.1, () => SetZoneFontSize(15));
            AddCheck("分区字号：大（18）", Math.Abs(fs - 18) < 0.1, () => SetZoneFontSize(18));
            AddCheck("分区字号：特大（21）", Math.Abs(fs - 21) < 0.1, () => SetZoneFontSize(21));

            AddSeparator();

            AddCheck("开机自动启动", AutoStart.IsEnabled(), () =>
            {
                AutoStart.Set(!AutoStart.IsEnabled());
                _data.Window.AutoStart = AutoStart.IsEnabled();
                SavePrefs();
            });

            AddAction("在桌面创建快捷方式", () =>
            {
                try
                {
                    string exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        string lnk = Path.Combine(Storage.DesktopDir, "DeskTidy.lnk");
                        Shortcut.Create(exe, lnk);
                    }
                }
                catch { }
            });
            AddAction("把所有图标放回桌面", () => ExportAllToDesktop());
            AddAction("打开数据文件夹", () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", Storage.Root); } catch { }
            });

            AddSeparator();

            AddAction("卸载 DeskTidy…", () => { SettingsPopup.IsOpen = false; UninstallApp(); });
            AddAction("退出 DeskTidy", () => { SettingsPopup.IsOpen = false; ExitApp(); });
        }

        /// <summary>把所有分区里的图标都搬回桌面，并清空分区条目。</summary>
        private void ExportAllToDesktop()
        {
            int ok = 0, fail = 0;

            foreach (Zone zone in _data.Zones)
            {
                foreach (ZoneItem item in zone.Items.ToList())
                {
                    if (TryMoveToDesktop(item)) ok++;
                    else fail++;
                }

                zone.Items.Clear();
                zone.NotifyCount();
            }

            if (_activeZone != null)
            {
                UpdateEmptyState();
                AnimateHeight(CollapsedHeight + PanelContentHeight(_activeZone));
            }

            SaveData();

            MessageBox.Show(
                fail == 0
                    ? $"已把 {ok} 个图标放回桌面。"
                    : $"已放回 {ok} 个，还有 {fail} 个没能放回（可能被占用）。",
                "放回桌面", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddCheck(string text, bool isChecked, Action onClick)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Accent"),
                Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed
            };

            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(check, 0);
            Grid.SetColumn(label, 1);
            grid.Children.Add(check);
            grid.Children.Add(label);

            var btn = new Button
            {
                Content = grid,
                Style = (Style)FindResource("MenuRowButton")
            };
            btn.Click += (s, e) =>
            {
                onClick?.Invoke();
                BuildSettingsMenu(); // 重建，让勾选立刻同步
            };

            SettingsStack.Children.Add(btn);
        }

        private void AddAction(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var btn = new Button
            {
                Content = label,
                Style = (Style)FindResource("MenuRowButton")
            };
            btn.Click += (s, e) => onClick?.Invoke();

            SettingsStack.Children.Add(btn);
        }

        private void AddSeparator()
        {
            SettingsStack.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(8, 4, 8, 4),
                Background = (Brush)FindResource("Divider")
            });
        }

        private DispatcherTimer _settingsCloseTimer;

        /// <summary>鼠标移出设置面板后延迟收起，划一下不会误关。</summary>
        private void ScheduleSettingsClose()
        {
            if (_settingsCloseTimer == null)
            {
                _settingsCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _settingsCloseTimer.Tick += (s, e) =>
                {
                    _settingsCloseTimer.Stop();
                    if (!SettingsPanel.IsMouseOver) SettingsPopup.IsOpen = false;
                };
            }

            _settingsCloseTimer.Stop();
            _settingsCloseTimer.Start();
        }

        private void SetTheme(int theme)
        {
            _data.Window.Theme = theme;
            ApplyTheme();
            ApplyBackdrop();
            SavePrefs();
        }

        // ============================================================
        //  生命周期
        // ============================================================

        private void SaveData() => Storage.Save(_data);

        private void SavePrefs()
        {
            _data.Window.Left = Left;
            _data.Window.Top = Top;
            _data.Window.Width = Width - ShadowPad * 2;
            _data.Window.Topmost = Topmost;
            Storage.Save(_data);
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SavePrefs();
            if (!_closingForReal)
            {
                e.Cancel = true;
                Hide();
            }
        }

        public void ShowPanel()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = false;
        }

        public void SnapToTopPublic()
        {
            SnapToTopCenter(true);
            SavePrefs();
        }

        private int TotalItemCount()
        {
            int n = 0;
            foreach (Zone z in _data.Zones) n += z.Items.Count;
            return n;
        }

        /// <summary>卸载引导：先确认图标都放回桌面了，再打开数据文件夹并退出。</summary>
        private void UninstallApp()
        {
            int total = TotalItemCount();

            if (total > 0)
            {
                var ask = MessageBox.Show(
                    $"还有 {total} 个程序分布在各个分区里。\n\n" +
                    "卸载 DeskTidy 时，这些快捷方式会跟着数据文件夹一起被删掉。\n" +
                    "建议先把它们放回桌面。\n\n现在就放回桌面吗？",
                    "卸载前检查", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (ask != MessageBoxResult.Yes) return;

                ExportAllToDesktop();

                if (TotalItemCount() > 0)
                {
                    MessageBox.Show(
                        "还有图标没能放回桌面，卸载流程已暂停。\n\n" +
                        "请手动把剩余的图标拖到桌面后再试一次。",
                        "卸载已取消", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var confirm = MessageBox.Show(
                "接下来会打开数据文件夹，并退出 DeskTidy。\n\n" +
                "请手动删除这两样：\n" +
                "  1. DeskTidy.exe（程序本体）\n" +
                "  2. %APPDATA%\\DeskTidy 文件夹\n\n" +
                "你的程序本体从来没有被移动过，删掉这两个不会影响它们。\n\n确定继续吗？",
                "卸载 DeskTidy", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (confirm != MessageBoxResult.OK) return;

            try { System.Diagnostics.Process.Start("explorer.exe", Storage.Root); } catch { }
            ExitApp();
        }

        public void ExitApp()
        {
            _closingForReal = true;
            SavePrefs();
            Close();
            Application.Current.Shutdown();
        }

        // ============================================================
        //  工具
        // ============================================================

        private static T FindVisual<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
        {
            if (root == null) return null;

            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && predicate(t)) return t;

                T deep = FindVisual(child, predicate);
                if (deep != null) return deep;
            }
            return null;
        }
    }
}
