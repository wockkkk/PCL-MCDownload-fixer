using System;
using System.Collections.Generic;
using System.IO;

namespace PCLauncher.Core
{
    public sealed class Options
    {
        public string PclPath;
        public string RulesPath;
        public string OutDir;
        public bool ScanOnly;
        public bool NoPatch;
        public bool NoUpdateBlock;
        public bool NoLoaderRestore;
        public bool NoModpackDialog;
        public bool ShowHelp;
        public bool SelfTest;
        public bool Diag;
        public string LauncherDir;
        public string TempDir;

        public const string HelpText =
            "PCL MCDownload fixer —— PCL 下载 / 整合包功能恢复宿主" + "\r\n" +
            "用法：" + "\r\n" +
            "  PCL-MCDownload-fixer.exe [选项]" + "\r\n" +
            "选项：" + "\r\n" +
            "  --pcl <路径>       指定 Plain Craft Launcher 2.exe" + "\r\n" +
            "  --rules <路径>     指定规则文件（默认 assets/rules.json）" + "\r\n" +
            "  --scan             只扫描并输出拦截点报告，不启动 PCL" + "\r\n" +
            "  --out <目录>       报告输出目录（默认 <Fixer>\runtime\reports）" + "\r\n" +
            "  --temp <目录>      指定 PCL 使用的临时目录（默认自动选择可写目录）" + "\r\n" +
            "  --no-patch         只做宿主，不打任何补丁（排障用）" + "\r\n" +
            "  --no-update-block  不做更新禁用" + "\r\n" +
            "  --no-loader       不做下载页加载器替换" + "\r\n" +
            "  --no-modpack      不做整合包手动安装入口恢复" + "\r\n" +
            "  --selftest         运行内置自检（整合包类型识别），不启动 PCL" + "\r\n" +
            "  --diag             诊断模式：记录 PCL 权限检查的真实异常" + "\r\n" +
            "  --help             显示本帮助";

        public static Options Parse(string[] args)
        {
            var o = new Options();
            o.LauncherDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--help": case "-h": o.ShowHelp = true; break;
                    case "--scan": o.ScanOnly = true; break;
                    case "--selftest": o.SelfTest = true; break;
                    case "--diag": o.Diag = true; break;
                    case "--no-patch": o.NoPatch = true; break;
                    case "--no-update-block": o.NoUpdateBlock = true; break;
                    case "--no-loader": o.NoLoaderRestore = true; break;
                    case "--no-modpack": o.NoModpackDialog = true; break;
                    case "--pcl": o.PclPath = Need(args, ref i, a); break;
                    case "--rules": o.RulesPath = Need(args, ref i, a); break;
                    case "--out": o.OutDir = Need(args, ref i, a); break;
                    case "--temp": o.TempDir = Need(args, ref i, a); break;
                    default:
                        // 未知参数只提示、不崩溃；也不再向 PCL 透传（PCL 自己读 Environment.GetCommandLineArgs）
                        Log.Warn("忽略未识别参数：" + a);
                        break;
                }
            }
            return o;
        }

        private static string Need(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(name + " 缺少参数值");
            return args[++i];
        }

        public static string LocatePcl(string launcherDir)
        {
            var candidates = new List<string>();
            candidates.Add(Path.Combine(launcherDir, "Plain Craft Launcher 2.exe"));
            candidates.Add(Path.Combine(launcherDir, "PCL", "Plain Craft Launcher 2.exe"));
            try
            {
                string cfg = Path.Combine(launcherDir, "PCL-MCDownload-fixer.json");
                if (File.Exists(cfg))
                {
                    string text = File.ReadAllText(cfg);
                    string p = Json.GetString(text, "pclPath");
                    if (!string.IsNullOrEmpty(p)) candidates.Insert(0, Path.IsPathRooted(p) ? p : Path.Combine(launcherDir, p));
                }
            }
            catch (Exception) { }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
