using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PCLauncher.Core;
using PCLauncher.Host;
using PCLauncher.Scan;

namespace PCLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Log.Start();
            Embedded.Hook();
            Embedded.ApplySwitches();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiblingAssembly;
            try
            {
                LoaderProbe();
                ForceModernTls();
                Log.Info("PCL MCDownload fixer 启动，参数：" + string.Join(" ", args));
                try
                {
                    var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    bool admin = new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    Log.Info("[env] 账户=" + id.Name + "，提权状态=" + (admin ? "已提升(Administrator)" : "未提升(过滤令牌)"));
                }
                catch (Exception ex) { Log.Warn("[env] 读取提权状态失败：" + ex.Message); }
                try
                {
                    Log.Info("[net] SecurityProtocol=" + System.Net.ServicePointManager.SecurityProtocol
                             + "，默认代理=" + (System.Net.WebRequest.DefaultWebProxy == null ? "<null>" : System.Net.WebRequest.DefaultWebProxy.GetProxy(new Uri("https://launchermeta.mojang.com/")).ToString())
                             + "，系统代理=" + (System.Net.WebRequest.GetSystemWebProxy() == null ? "<null>" : System.Net.WebRequest.GetSystemWebProxy().GetProxy(new Uri("https://launchermeta.mojang.com/")).ToString())
                             + "，证书回调=" + (System.Net.ServicePointManager.ServerCertificateValidationCallback == null ? "默认" : "已自定义"));
                }
                catch (Exception ex) { Log.Warn("[net] 读取网络配置失败：" + ex.Message); }

                Options opt;
                try { opt = Options.Parse(args); }
                catch (Exception ex)
                {
                    Log.Error("参数解析失败", ex);
                    Console.Error.WriteLine(ex.Message);
                    return 2;
                }

                if (opt.SelfTest)
                {
                    int code = Core.SelfTest.RunModpackDetection(Path.Combine(opt.LauncherDir, "temp", "selftest"));
                    Log.Info("自检结束，exit=" + code);
                    return code;
                }

                if (opt.ShowHelp)
                {
                    Console.WriteLine(Options.HelpText);
                    return 0;
                }

                string pclExe = opt.PclPath;
                if (pclExe == null) pclExe = Options.LocatePcl(opt.LauncherDir);
                if (pclExe == null)
                {
                    Log.Error("未能定位 PCL 主程序（Plain Craft Launcher 2.exe）。");
                    MessageBox.Show("未能定位 PCL 主程序。" + Environment.NewLine +
                                    "请把 PCL-MCDownload-fixer.exe 放在 PCL 目录中，或用 --pcl 参数指定路径。",
                        "PCL MCDownload fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }

                string pclDir = Path.GetDirectoryName(Path.GetFullPath(pclExe));
                string runtimeDir = Path.Combine(opt.LauncherDir, "runtime");
                Log.Info("[paths] Fixer 目录=" + opt.LauncherDir + "，运行时目录=" + runtimeDir);
                string beforeHash = Crypto.Sha256File(pclExe);
                Log.Info("目标 PCL：" + pclExe);
                Log.Info("目标 SHA256（启动前）：" + beforeHash);

                Rules rules = Rules.Load(opt.RulesPath, opt);
                EnvironmentPrep.PrepareTemp(opt, pclDir);
                if (rules.PermissionCompat) Environment.SetEnvironmentVariable("PCLAUNCHER_PERM_COMPAT", "1");
                if (opt.Diag) { Environment.SetEnvironmentVariable("PCLAUNCHER_DIAG", "1"); EnvironmentPrep.Probe(pclDir); }

                if (opt.ScanOnly)
                {
                    ScanReport scan = PclScanner.Scan(pclExe, rules);
                    ScanReportWriter.Write(scan, opt.OutDir ?? Path.Combine(runtimeDir, "reports"), beforeHash);
                    Console.WriteLine(ScanReportWriter.ToText(scan));
                    Log.Info("扫描完成，报告已写入 " + (opt.OutDir ?? pclDir));
                    return 0;
                }

                int exitCode = PclHost.Run(pclExe, opt, rules, beforeHash, runtimeDir);

                string afterHash = Crypto.Sha256File(pclExe);
                Log.Info("目标 SHA256（退出后）：" + (afterHash ?? "<计算失败>"));
                if (beforeHash == null || afterHash == null)
                    Log.Warn("哈希校验未完成（无法读取文件），本次不做“未修改”结论。");
                else if (afterHash == beforeHash)
                    Log.Info("校验通过：官方 exe 未被修改。");
                else
                    Log.Error("警告：官方 exe 哈希发生变化！");
                return exitCode;
            }
            catch (Exception ex)
            {
                Log.Error("未处理的异常", ex);
                MessageBox.Show(ex.ToString(), "PCL MCDownload fixer 崩溃", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                Log.Stop();
            }
        }

        /// <summary>强制启用现代 TLS：宿主进程没有声明目标框架时，.NET 只开 Ssl3/TLS1.0，访问 Mojang/BMCLAPI 必然握手失败。</summary>
        private static void ForceModernTls()
        {
            try
            {
                var proto = System.Net.SecurityProtocolType.Tls12;
                try { proto |= (System.Net.SecurityProtocolType)12288; } catch (Exception) { }   // Tls13
                System.Net.ServicePointManager.SecurityProtocol = proto;
                Log.Info("[net] 已强制 SecurityProtocol=" + System.Net.ServicePointManager.SecurityProtocol);
            }
            catch (Exception ex) { Log.Warn("[net] 设置 TLS 失败：" + ex.Message); }
        }

        /// <summary>依赖缺失时从 launcher 自身目录兜底加载（防止被杀软/解压/复制遗漏后直接崩溃）。</summary>
        private static Assembly ResolveSiblingAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string file = Path.Combine(dir, name);
                if (File.Exists(file))
                {
                    Log.Warn("[loader] 通过目录兜底加载依赖：" + file);
                    return Assembly.LoadFrom(file);
                }
                Log.Error("[loader] 缺少依赖程序集：" + args.Name + "（应位于 " + dir + "）");
            }
            catch (Exception ex) { Log.Warn("[loader] 兜底加载失败：" + ex.Message); }
            return null;
        }

        /// <summary>启动时检查必需依赖是否齐全可加载，缺失时给出明确提示而不是崩溃。</summary>
        private static void LoaderProbe()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var required = new[] { "Mono.Cecil", "0Harmony" };
            var missing = new System.Collections.Generic.List<string>();
            foreach (var name in required)
            {
                // 单文件发布：直接来自内嵌资源
                if (Embedded.Has(name)) { Log.Info("[loader] " + name + " 使用内嵌资源"); continue; }
                string p = Path.Combine(dir, name + ".dll");
                if (!File.Exists(p)) { missing.Add(name + ".dll（既没内嵌也没有文件）"); continue; }
                try { Assembly.LoadFrom(p); }
                catch (Exception ex) { missing.Add(name + ".dll（" + ex.GetType().Name + "：" + ex.Message.Split('\n')[0] + "）"); }
            }
            if (missing.Count > 0)
            {
                string msg = "PCL MCDownload fixer 缺少依赖：" + Environment.NewLine + string.Join(Environment.NewLine, missing)
                             + Environment.NewLine + Environment.NewLine + "请使用完整发行包（单文件版本无需其它 dll）。"
                             + Environment.NewLine + "目录：" + dir;
                Log.Error("[loader] " + msg.Replace(Environment.NewLine, " | "));
                MessageBox.Show(msg, "PCL MCDownload fixer 依赖缺失", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(4);
            }
            Log.Info("[loader] 依赖检查通过（" + string.Join(", ", required) + "）。");
        }
    }
}
