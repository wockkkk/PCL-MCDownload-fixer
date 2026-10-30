using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PCLauncher.Core;
using PCLauncher.Scan;

namespace PCLauncher.Update
{
    /// <summary>更新禁用：把 ModSecret 中的更新入口全部 prefix 跳过。</summary>
    public static class UpdateBlocker
    {
        public static int Apply(Harmony harmony, Assembly pcl, ScanReport report, Rules rules, List<string> applied, List<string> failed)
        {
            if (!rules.UpdateBlock.Enabled) return 0;
            int count = 0;
            foreach (var u in report.UpdateTargets)
            {
                string id = u.DeclaringType + "::" + u.MethodName;
                try
                {
                    var mb = pcl.ManifestModule.ResolveMethod(u.Token);
                    harmony.Patch(mb, prefix: new HarmonyMethod(typeof(Patch.Patches).GetMethod("SkipPrefix")));
                    applied.Add(id + " —— 更新禁用");
                    Log.Info("[update] 已禁用 " + id);
                    count++;
                }
                catch (Exception ex)
                {
                    failed.Add(id + " —— " + ex.Message);
                    Log.Error("[update] 禁用失败 " + id, ex);
                }
            }
            return count;
        }
    }
}
