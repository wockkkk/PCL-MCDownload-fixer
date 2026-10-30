using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PCLauncher.Core;
using PCLauncher.Patch;
using PCLauncher.Scan;

namespace PCLauncher.Host
{
    /// <summary>
    /// 在独立 AppDomain 中运行：ApplicationBase 指向 PCL 目录，
    /// 先完成扫描与补丁，再反射调用 PCL 入口点，保证补丁先于 JIT。
    /// </summary>
    public sealed class DomainRunner : MarshalByRefObject
    {
        public int Run(string pclExe, string rulesPath, string launcherDir,
                       bool noLoader, bool noUpdate, bool noModpack, bool enablePatch,
                       string reportDir, string logPath)
        {
            Embedded.Hook();
            Embedded.ApplySwitches();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLauncherDir;
            Log.Attach(logPath);
            Log.Info("[domain] 已接管日志文件（追加写）。");

            string dir = Path.GetDirectoryName(Path.GetFullPath(pclExe));
            var opt = new Options
            {
                LauncherDir = launcherDir,
                NoLoaderRestore = noLoader,
                NoUpdateBlock = noUpdate,
                NoModpackDialog = noModpack
            };
            Rules rules = Rules.Load(rulesPath, opt);

            Log.Info("[domain] 当前域：" + AppDomain.CurrentDomain.FriendlyName + "，Base=" + AppDomain.CurrentDomain.BaseDirectory);

            // 1) 只读分析磁盘文件（Cecil），不触碰运行时
            ScanReport report = PclScanner.Scan(pclExe, rules);
            Log.Info("[scan] " + report.AssemblyName + " " + report.AssemblyVersion + "，拦截点 " + report.Sites.Count + " 处。");
            foreach (var s in report.Sites)
                Log.Info("[scan] " + s.KindText + "  " + s.DeclaringType + "::" + s.MethodName + "  0x" + s.Token.ToString("X8"));
            foreach (var w in report.Warnings) Log.Warn("[scan] " + w);

            string actualReportDir = reportDir ?? Path.Combine(launcherDir, "runtime", "reports");
            ScanReportWriter.Write(report, actualReportDir, report.Sha256);
            Log.Info("[scan] 报告目录：" + actualReportDir);

            // 2) 载入程序集（此时尚未 JIT 任何 PCL 代码）
            Log.Info("[host] Assembly.LoadFrom " + pclExe);
            Assembly pcl;
            try
            {
                pcl = Assembly.LoadFrom(pclExe);
            }
            catch (FileLoadException exLoad)
            {
                // 网络盘 / 带 MOTW 的 exe：CAS 会拒绝 LoadFrom；UnsafeLoadFrom 会跳过远程加载检查且保留 Location
                Log.Warn("[host] LoadFrom 被拒绝，改用 UnsafeLoadFrom：" + exLoad.Message.Split('\n')[0]);
                try { pcl = Assembly.UnsafeLoadFrom(pclExe); }
                catch (Exception exUnsafe)
                {
                    Log.Error("[host] UnsafeLoadFrom 也失败，退回字节加载", exUnsafe);
                    pcl = Assembly.Load(File.ReadAllBytes(pclExe));
                }
            }
            DependencyResolver.Preload(pcl);

            // 3) 打补丁
            PatchResult pr = PatchEngine.Apply(pcl, report, rules, enablePatch);
            foreach (var a in pr.Applied) Log.Info("[patch+] " + a);
            foreach (var f in pr.Failed) Log.Warn("[patch-] " + f);

            // 3.5) WPF 宿主关键一步：把 Application.ResourceAssembly 指向 PCL 程序集
            //     否则 SplashScreen / pack URI 会去 PCLauncher.exe 里找资源，直接抛 ArgumentNullException。
            TrySetResourceAssembly(pcl);

            // 4) 调用入口点
            MethodInfo entry = pcl.EntryPoint;
            if (entry == null)
            {
                var appType = pcl.GetType("PCL.Application", false, true);
                if (appType != null)
                    entry = appType.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (entry == null)
                throw new InvalidOperationException("未找到 PCL 入口点。");

            Log.Info("[host] 调用入口点 " + entry.DeclaringType.FullName + "::" + entry.Name + "（STA 线程：" + (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA) + "）");
            // PCL 的 Main() 无参，命令行由它自己用 Environment.GetCommandLineArgs() 读取，这里不透传
            object ret = entry.Invoke(null, null);
            Log.Info("[host] 入口点返回：" + (ret == null ? "void" : ret.ToString()));
            return 0;
        }

        private static void TrySetResourceAssembly(Assembly pcl)
        {
            try
            {
                var appType = Type.GetType("System.Windows.Application, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35", false);
                if (appType == null) { Log.Warn("[host] 未找到 PresentationFramework，跳过 ResourceAssembly 设置。"); return; }
                var prop = appType.GetProperty("ResourceAssembly", BindingFlags.Public | BindingFlags.Static);
                if (prop == null) { Log.Warn("[host] 未找到 Application.ResourceAssembly 属性。"); return; }
                var current = prop.GetValue(null, null);
                if (current == null)
                {
                    prop.SetValue(null, pcl, null);
                    Log.Info("[host] Application.ResourceAssembly = " + pcl.GetName().Name);
                }
                else Log.Info("[host] Application.ResourceAssembly 已设置：" + ((Assembly)current).GetName().Name);
            }
            catch (Exception ex) { Log.Warn("[host] 设置 Application.ResourceAssembly 失败：" + ex.GetType().Name + " " + ex.Message); }
        }

        private Assembly ResolveFromLauncherDir(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
                string pclDir = AppDomain.CurrentDomain.BaseDirectory;
                p = Path.Combine(pclDir, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch (Exception ex) { Log.Warn("[resolve] " + args.Name + " -> " + ex.Message); }
            return null;
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
