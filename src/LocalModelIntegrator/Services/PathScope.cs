using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Resolves a filesystem path to its canonical on-disk form, following symbolic links,
    /// junctions and mount points, so scope checks compare real targets rather than the literal
    /// string a caller supplied. A junction placed inside the solution folder that points at
    /// C:\secrets resolves to its real location (and so fails the in-solution test), and a
    /// solution file reached through a linked path still matches the solution's real path.
    ///
    /// Falls back to <see cref="Path.GetFullPath"/> — which only normalizes '..'/casing and does
    /// NOT follow links — when the path cannot be opened (e.g. it does not exist). That is safe:
    /// a path that cannot be opened cannot be an in-solution file we would read anyway.
    /// </summary>
    internal static class PathScope
    {
        /// <summary>
        /// Canonical full path (links resolved, no trailing separator), or null when
        /// <paramref name="path"/> is empty or cannot be normalized at all.
        /// </summary>
        public static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string viaHandle = TryGetFinalPath(path);
            if (!string.IsNullOrEmpty(viaHandle))
                return Trim(viaHandle);

            try { return Trim(Path.GetFullPath(path)); }
            catch { return null; }
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? s : s.TrimEnd(Path.DirectorySeparatorChar);

        private static string TryGetFinalPath(string path)
        {
            SafeFileHandle handle = null;
            try
            {
                handle = CreateFileW(path, 0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (handle.IsInvalid)
                    return null;

                var sb = new StringBuilder(512);
                uint len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
                if (len == 0)
                    return null;
                if (len >= sb.Capacity)
                {
                    sb = new StringBuilder((int)len + 1);
                    len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
                    if (len == 0)
                        return null;
                }
                return StripExtendedPrefix(sb.ToString());
            }
            catch
            {
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        // GetFinalPathNameByHandle returns an extended-length ("\\?\") path; strip the prefix so
        // it compares equal to the ordinary paths DTE/Roslyn report.
        private static string StripExtendedPrefix(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + s.Substring(8);
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
                return s.Substring(4);
            return s;
        }

        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // lets us open directories too
        private const uint FILE_NAME_NORMALIZED = 0x0;
        private const uint VOLUME_NAME_DOS = 0x0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
    }
}
