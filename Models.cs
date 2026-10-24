using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskTidy
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public class Zone : ObservableObject
    {
        private string _name = "新分区";
        private bool _isSelected;
        private bool _isEditing;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get => _name; set => Set(ref _name, value); }
        public ObservableCollection<ZoneItem> Items { get; set; } = new ObservableCollection<ZoneItem>();

        [JsonIgnore] public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        [JsonIgnore] public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
        [JsonIgnore] public int Count => Items?.Count ?? 0;

        public void NotifyCount() => Raise(nameof(Count));
    }

    public class ZoneItem : ObservableObject
    {
        private string _displayName = "";
        private BitmapSource _icon;
        private bool _iconLoaded;
        private bool _isDragSource;
        private bool _isEditing;
        private bool _isSelected;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>当前实际使用的路径（可能已被移动到 Library 中）。</summary>
        public string Path { get; set; } = "";

        /// <summary>拖入时的原始路径，用于"拖出到桌面"时还原位置。</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>是否已由本程序接管（文件已移动到 Library，可安全搬回）。</summary>
        public bool Managed { get; set; }

        public string DisplayName
        {
            get => _displayName;
            set { if (Set(ref _displayName, value)) Raise(nameof(Initial)); }
        }

        [JsonIgnore]
        public string Initial
        {
            get
            {
                var n = _displayName?.Trim();
                return string.IsNullOrEmpty(n) ? "?" : n.Substring(0, 1).ToUpperInvariant();
            }
        }

        [JsonIgnore]
        public BitmapSource Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    _icon = IconCache.Get(Path);
                    Raise(nameof(Icon));
                }
                return _icon;
            }
        }

        [JsonIgnore] public bool IsDragSource { get => _isDragSource; set => Set(ref _isDragSource, value); }
        [JsonIgnore] public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
        [JsonIgnore] public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        [JsonIgnore] public bool Exists => !string.IsNullOrEmpty(Path) && (File.Exists(Path) || Directory.Exists(Path));

        /// <summary>供拖拽/显示使用的名字（去掉扩展名更干净，但 .exe 保留辨识度）。</summary>
        public static string MakeDisplayName(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }

    public class WindowPrefs
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double Width { get; set; } = 620;
        public bool Snapped { get; set; } = true;
        public bool Topmost { get; set; } = true;   // 默认置顶：Win+D 时才不会被桌面层盖住
        public bool AutoStart { get; set; } = false;
        /// <summary>0 = 跟随系统, 1 = 深色, 2 = 浅色</summary>
        public int Theme { get; set; } = 0;

        /// <summary>分区胶囊的字号</summary>
        public double ZoneFontSize { get; set; } = 12.5;
    }

    public class AppData
    {
        public int Version { get; set; } = 1;
        public ObservableCollection<Zone> Zones { get; set; } = new ObservableCollection<Zone>();
        public WindowPrefs Window { get; set; } = new WindowPrefs();
    }
}
