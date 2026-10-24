using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskTidy
{
    /// <summary>用 IShellLink 生成 .lnk，避免复制大文件，也避免把 exe 搬来搬去。</summary>
    internal static class Shortcut
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        /// <summary>创建指向 targetPath 的快捷方式。</summary>
        public static bool Create(string targetPath, string lnkPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(lnkPath)) return false;

            object link = null;
            try
            {
                link = new CShellLink();
                var shellLink = (IShellLinkW)link;

                shellLink.SetPath(targetPath);
                try
                {
                    string dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir)) shellLink.SetWorkingDirectory(dir);
                }
                catch { }
                shellLink.SetDescription(Path.GetFileNameWithoutExtension(targetPath));

                ((IPersistFile)link).Save(lnkPath, true);
                return File.Exists(lnkPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (link != null)
                    try { Marshal.ReleaseComObject(link); } catch { }
            }
        }

        /// <summary>解析 .lnk 指向的真实目标（失败则返回原路径）。</summary>
        public static string ResolveTarget(string lnkPath)
        {
            object link = null;
            try
            {
                link = new CShellLink();
                var persist = (IPersistFile)link;
                persist.Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                string p = sb.ToString();
                return string.IsNullOrWhiteSpace(p) ? lnkPath : p;
            }
            catch { return lnkPath; }
            finally
            {
                if (link != null)
                    try { Marshal.ReleaseComObject(link); } catch { }
            }
        }
    }
}
