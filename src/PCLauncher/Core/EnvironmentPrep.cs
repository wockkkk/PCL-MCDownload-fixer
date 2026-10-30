using System;
using System.IO;

namespace PCLauncher.Core
{
    /// <summary>
    /// PCL 启动时会检查临时目录可写性。若系统临时目录被策略/沙箱限制，
    /// 统一改写到可写目录，避免 PCL 弹出“运行环境错误”。
    /// </summary>
    public static class EnvironmentPrep
    {
        public static void PrepareTemp(Options opt, string pclDir)
        {
            string dir = opt.TempDir;
            if (string.IsNullOrEmpty(dir))
            {
                string current = Path.GetTempPath();
                if (!CanWrite(current))
                {
                    dir = Path.Combine(opt.LauncherDir, "runtime", "temp");
                }
            }

            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                Environment.SetEnvironmentVariable("TEMP", dir);
                Environment.SetEnvironmentVariable("TMP", dir);
                Log.Info("[env] 临时目录已切换到 " + dir);
            }
            catch (Exception ex)
            {
                Log.Warn("[env] 设置临时目录失败：" + ex.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr sa);

        /// <summary>诊断：逐一验证各种写入方式是否可用（定位“运行环境错误”的真正原因）。</summary>
        public static void Probe(string pclDir)
        {
            string baseDir = Path.Combine(pclDir, "PCL");
            Try("普通 CreateDirectory", delegate { Directory.CreateDirectory(Path.Combine(baseDir, "Pictures")); });
            Try("普通 写入+删除", delegate
            {
                string f = Path.Combine(baseDir, "probe-normal.txt");
                File.WriteAllText(f, "x"); File.Delete(f);
            });
            Try("长路径 写入+删除", delegate
            {
                string f = @"\\?\" + Path.Combine(baseDir, "probe-long.txt");
                File.WriteAllText(f, "x"); File.Delete(f);
            });
            Try("Win32 CreateDirectoryW(普通)", delegate
            {
                if (!CreateDirectoryW(Path.Combine(baseDir, "ProbeWin32"), IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            });
            Try("Win32 CreateDirectoryW(长路径)", delegate
            {
                if (!CreateDirectoryW(@"\\?\" + Path.Combine(baseDir, "ProbeWin32Long"), IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            });
        }

        private static void Try(string what, Action a)
        {
            try { a(); Log.Info("[probe] OK   " + what); }
            catch (Exception ex) { Log.Warn("[probe] FAIL " + what + " -> " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "PCLMCFixer-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
