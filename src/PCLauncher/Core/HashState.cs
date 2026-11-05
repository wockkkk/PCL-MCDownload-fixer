using System;
using System.IO;

namespace PCLauncher.Core
{
    /// <summary>
    /// “官方 exe 未被修改”的跨次启动校验。
    /// PCL 退出时会直接调 Environment.Exit，进程级的“退出后哈希”实测并不可靠
    /// （AppDomain.ProcessExit 不触发，钩 Environment.Exit 也不生效），
    /// 所以改成：每次启动前把哈希记到 runtime\state\last-run.sha256，
    /// 下次启动时先与上次记录的哈希比对——一致即可证明两次运行之间 exe 没有被改写。
    /// </summary>
    public static class HashState
    {
        public static void VerifyPrevious(string pclExe, string beforeHash, string runtimeDir)
        {
            string file = Path.Combine(runtimeDir, "state", "last-run.sha256");
            try
            {
                if (string.IsNullOrEmpty(beforeHash))
                {
                    Log.Warn("[hash] 本次启动前无法计算哈希，跳过与上次记录的比对。");
                    return;
                }
                if (File.Exists(file))
                {
                    string[] lines = File.ReadAllLines(file);
                    string prev = lines.Length > 0 ? lines[0].Trim() : null;
                    string when = lines.Length > 1 ? lines[1].Trim() : "<未知时间>";
                    if (string.Equals(prev, beforeHash, StringComparison.OrdinalIgnoreCase))
                        Log.Info("[hash] 校验通过：与上次运行（" + when + "）记录的 SHA256 一致，官方 exe 未被修改。");
                    else
                        Log.Error("[hash] 警告：官方 exe 的 SHA256 与上次运行记录不一致！上次 " + (prev ?? "<空>") + "，本次 " + beforeHash);
                }
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(file, new[] { beforeHash, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                Log.Info("[hash] 已记录本次启动前的 SHA256，供下次启动校验：" + file);
            }
            catch (Exception ex)
            {
                Log.Warn("[hash] 跨次哈希校验失败：" + ex.Message);
            }
        }

        /// <summary>PCL 正常返回（没有走 Environment.Exit）时，当场再校验一次。</summary>
        public static void VerifyAfterRun(string pclExe, string beforeHash)
        {
            try
            {
                string after = Crypto.Sha256File(pclExe);
                Log.Info("[hash] 目标 SHA256（返回后）：" + (after ?? "<计算失败>"));
                if (beforeHash == null || after == null)
                    Log.Warn("[hash] 哈希校验未完成（无法读取文件），本次不做“未修改”结论。");
                else if (string.Equals(after, beforeHash, StringComparison.OrdinalIgnoreCase))
                    Log.Info("[hash] 校验通过：官方 exe 未被修改。");
                else
                    Log.Error("[hash] 警告：官方 exe 的 SHA256 发生变化！");
            }
            catch (Exception ex)
            {
                Log.Warn("[hash] 返回后计算哈希失败：" + ex.Message);
            }
        }
    }
}
