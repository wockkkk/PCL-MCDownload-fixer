using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PCLauncher.Core;

namespace PCLauncher.Scan
{
    public static class ScanReportWriter
    {
        public static string BaseName(ScanReport r)
        {
            string ver = r.AssemblyVersion.Replace('.', '_');
            return "PCL-scan-" + ver;
        }

        /// <summary>写报告；任何 IO 失败都不影响启动，只记录告警。</summary>
        public static bool Write(ScanReport r, string outDir, string sha256)
        {
            string name = BaseName(r);
            foreach (var dir in new[] { outDir, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "reports") })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, name + ".json"), ToJson(r), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(dir, name + ".txt"), ToText(r), new UTF8Encoding(false));
                    if (!string.IsNullOrEmpty(sha256))
                        File.WriteAllText(Path.Combine(dir, name + ".sha256.txt"), sha256 + "  " + r.FilePath + Environment.NewLine, new UTF8Encoding(false));
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[report] 写入 " + dir + " 失败：" + ex.Message);
                }
            }
            return false;
        }

        public static string ToText(ScanReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ PCL 拦截点扫描报告 ================");
            sb.AppendLine("扫描时间    : " + r.ScannedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("文件        : " + r.FilePath);
            sb.AppendLine("大小        : " + r.FileSize + " 字节");
            sb.AppendLine("SHA256      : " + r.Sha256);
            sb.AppendLine("程序集      : " + r.AssemblyName + " " + r.AssemblyVersion);
            sb.AppendLine("运行时      : " + r.RuntimeVersion + " / " + r.Architecture);
            sb.AppendLine("入口点      : " + r.EntryPoint);
            sb.AppendLine("类型/方法/字段: " + r.TypeCount + " / " + r.MethodCount + " / " + r.FieldCount);
            sb.AppendLine("规则来源    : " + r.RulesSource);
            sb.AppendLine();
            sb.AppendLine("---- 拦截点（特征字符串） " + r.Sites.Count + " 处 ----");
            foreach (var s in r.Sites)
            {
                sb.AppendLine(string.Format("  [{0}] {1}::{2}", s.KindText, s.DeclaringType, s.MethodName));
                sb.AppendLine(string.Format("      token=0x{0:X8}  sig={1}  IL={2} 字节", s.Token, s.Signature, s.IlSize));
                sb.AppendLine("      指令窗口: " + s.Window);
                sb.AppendLine("      处置: " + s.Action);
            }
            if (r.Sites.Count == 0) sb.AppendLine("  （无）");
            sb.AppendLine();
            sb.AppendLine("---- 下载页加载器 ----");
            if (r.Loader.Present)
            {
                sb.AppendLine(string.Format("  {0}::{1} token=0x{2:X8}", r.Loader.DeclaringType, r.Loader.MethodName, r.Loader.Token));
                sb.AppendLine("  伪加载器标题: " + (r.Loader.DummyTitle ?? "<none>"));
                sb.AppendLine("  伪加载器构造: " + (r.Loader.DummyCtor ?? "<none>"));
                sb.AppendLine("  目标加载器字段: " + r.Loader.LoaderField);
                sb.AppendLine("  模式匹配: " + (r.Loader.PatternMatched ? "成功" : "失败（将回退为跳过）"));
                foreach (var n in r.Loader.Newobjs) sb.AppendLine("  newobj: " + n);
                foreach (var c in r.Loader.Calls) sb.AppendLine("  call: " + c);
            }
            else sb.AppendLine("  （无）");
            sb.AppendLine();
            sb.AppendLine("---- 更新禁用目标 " + r.UpdateTargets.Count + " 个 ----");
            foreach (var u in r.UpdateTargets)
                sb.AppendLine(string.Format("  {0}::{1}  token=0x{2:X8}  sig={3}", u.DeclaringType, u.MethodName, u.Token, u.Signature));
            if (r.UpdateTargets.Count == 0) sb.AppendLine("  （无）");
            sb.AppendLine();
            if (r.Warnings.Count > 0)
            {
                sb.AppendLine("---- 警告 ----");
                foreach (var w in r.Warnings) sb.AppendLine("  ! " + w);
            }
            if (r.Notes.Count > 0)
            {
                sb.AppendLine("---- 备注 ----");
                foreach (var n in r.Notes) sb.AppendLine("  - " + n);
            }
            return sb.ToString();
        }

        public static string ToJson(ScanReport r)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"scannedAt\":").Append(Json.Escape(r.ScannedAt.ToString("o"))).Append(",");
            sb.Append("\"file\":").Append(Json.Escape(r.FilePath)).Append(",");
            sb.Append("\"size\":").Append(r.FileSize).Append(",");
            sb.Append("\"sha256\":").Append(Json.Escape(r.Sha256)).Append(",");
            sb.Append("\"assembly\":").Append(Json.Escape(r.AssemblyName)).Append(",");
            sb.Append("\"version\":").Append(Json.Escape(r.AssemblyVersion)).Append(",");
            sb.Append("\"runtime\":").Append(Json.Escape(r.RuntimeVersion)).Append(",");
            sb.Append("\"architecture\":").Append(Json.Escape(r.Architecture)).Append(",");
            sb.Append("\"entryPoint\":").Append(Json.Escape(r.EntryPoint)).Append(",");
            sb.Append("\"types\":").Append(r.TypeCount).Append(",");
            sb.Append("\"methods\":").Append(r.MethodCount).Append(",");
            sb.Append("\"fields\":").Append(r.FieldCount).Append(",");
            sb.Append("\"rulesSource\":").Append(Json.Escape(r.RulesSource)).Append(",");

            sb.Append("\"sites\":[");
            for (int i = 0; i < r.Sites.Count; i++)
            {
                var s = r.Sites[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append("\"token\":\"0x").Append(s.Token.ToString("X8")).Append("\",");
                sb.Append("\"declaringType\":").Append(Json.Escape(s.DeclaringType)).Append(",");
                sb.Append("\"method\":").Append(Json.Escape(s.MethodName)).Append(",");
                sb.Append("\"signature\":").Append(Json.Escape(s.Signature)).Append(",");
                sb.Append("\"kind\":").Append(Json.Escape(s.KindText)).Append(",");
                sb.Append("\"ilSize\":").Append(s.IlSize).Append(",");
                sb.Append("\"window\":").Append(Json.Escape(s.Window)).Append(",");
                sb.Append("\"action\":").Append(Json.Escape(s.Action));
                sb.Append("}");
            }
            sb.Append("],");

            sb.Append("\"loader\":{");
            sb.Append("\"present\":").Append(r.Loader.Present ? "true" : "false").Append(",");
            sb.Append("\"token\":\"0x").Append(r.Loader.Token.ToString("X8")).Append("\",");
            sb.Append("\"declaringType\":").Append(Json.Escape(r.Loader.DeclaringType ?? "")).Append(",");
            sb.Append("\"method\":").Append(Json.Escape(r.Loader.MethodName ?? "")).Append(",");
            sb.Append("\"dummyTitle\":").Append(Json.Escape(r.Loader.DummyTitle ?? "")).Append(",");
            sb.Append("\"dummyCtor\":").Append(Json.Escape(r.Loader.DummyCtor ?? "")).Append(",");
            sb.Append("\"loaderField\":").Append(Json.Escape(r.Loader.LoaderField ?? "")).Append(",");
            sb.Append("\"patternMatched\":").Append(r.Loader.PatternMatched ? "true" : "false");
            sb.Append("},");

            sb.Append("\"updateTargets\":[");
            for (int i = 0; i < r.UpdateTargets.Count; i++)
            {
                var u = r.UpdateTargets[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append("\"token\":\"0x").Append(u.Token.ToString("X8")).Append("\",");
                sb.Append("\"declaringType\":").Append(Json.Escape(u.DeclaringType)).Append(",");
                sb.Append("\"method\":").Append(Json.Escape(u.MethodName)).Append(",");
                sb.Append("\"signature\":").Append(Json.Escape(u.Signature));
                sb.Append("}");
            }
            sb.Append("],");

            sb.Append("\"warnings\":[");
            for (int i = 0; i < r.Warnings.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Escape(r.Warnings[i]));
            }
            sb.Append("],");

            sb.Append("\"notes\":[");
            for (int i = 0; i < r.Notes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Escape(r.Notes[i]));
            }
            sb.Append("]");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
