using System;
using System.Runtime.InteropServices;

namespace DeskTidy
{
    /// <summary>
    /// 调用 Windows 原生 Shell 上下文菜单，让分区里的图标拥有和桌面上一模一样的右键功能
    /// （打开方式 / 属性 / 复制 / 剪切 / 删除 / 发送到 / 压缩 …），
    /// 再在末尾追加几条本程序自己的操作。
    /// </summary>
    internal static class ShellMenu
    {
        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc,
            out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHBindToParent(
            IntPtr pidl, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv,
            out IntPtr ppidlLast);

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        private interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
                [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            void GetUIObjectOf(IntPtr hwndOwner, uint cidl, ref IntPtr apidl,
                ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr pszName, int cch);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CMINVOKECOMMANDINFO
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
        }

        /// <summary>按系统谓词执行命令，例如 open / openas / copy / cut / delete / properties / link。</summary>
        public static bool InvokeVerb(IntPtr ownerHwnd, string path, string verb)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(verb)) return false;
            if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return false;

            IntPtr pidl = IntPtr.Zero;
            object cmObj = null;
            IntPtr verbPtr = IntPtr.Zero;

            try
            {
                uint attrs;
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) != 0 || pidl == IntPtr.Zero)
                    return false;

                var iidFolder = typeof(IShellFolder).GUID;
                if (SHBindToParent(pidl, ref iidFolder, out IShellFolder parent, out IntPtr childPidl) != 0 || parent == null)
                    return false;

                var iidCm = typeof(IContextMenu).GUID;
                parent.GetUIObjectOf(ownerHwnd, 1, ref childPidl, ref iidCm, IntPtr.Zero, out IntPtr cmPtr);
                if (cmPtr == IntPtr.Zero) return false;

                cmObj = Marshal.GetObjectForIUnknown(cmPtr);
                Marshal.Release(cmPtr);

                if (!(cmObj is IContextMenu cm)) return false;

                verbPtr = Marshal.StringToHGlobalAnsi(verb);

                var ci = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFO)),
                    hwnd = ownerHwnd,
                    lpVerb = verbPtr,
                    nShow = 1
                };

                IntPtr pci = Marshal.AllocHGlobal(ci.cbSize);
                try
                {
                    Marshal.StructureToPtr(ci, pci, false);
                    return cm.InvokeCommand(pci) == 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(pci);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (verbPtr != IntPtr.Zero) Marshal.FreeHGlobal(verbPtr);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                if (cmObj != null)
                    try { Marshal.ReleaseComObject(cmObj); } catch { }
            }
        }

    }
}
