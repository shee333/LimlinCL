using System;
using System.Diagnostics;

namespace Limlin.Link
{
    /// <summary>进程工具：读取可执行文件路径（替代 启动器的 LauncherProcess）。</summary>
    internal static class LauncherProcess
    {
        public static string TryGetImagePath(Process process)
        {
            if (process == null) return null;
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>当前启动器进程的可执行文件路径。</summary>
        public static string CurrentPath
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule?.FileName; }
                catch { return null; }
            }
        }
    }
}