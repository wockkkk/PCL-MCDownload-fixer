using System;
using System.IO;
using System.Reflection;
using PCLauncher.Core;
using PCLauncher.Scan;

namespace PCLauncher.Host
{
    public static class PclHost
    {
        public static int Run(string pclExe, Options opt, Rules rules, string beforeHash, string runtimeDir)
        {
            string pclDir = Path.GetDirectoryName(Path.GetFullPath(pclExe));
            string launcherDir = opt.LauncherDir;
            string cfg = pclExe + ".config";
            if (!File.Exists(cfg))
            {
                string ownCfg = Path.Combine(launcherDir, Path.GetFileName(Assembly.GetExecutingAssembly().Location) + ".config");
                if (File.Exists(ownCfg)) cfg = ownCfg;
            }


            Environment.CurrentDirectory = pclDir;

            var setup = new AppDomainSetup
            {
                ApplicationBase = pclDir,
                ApplicationName = "Plain Craft Launcher 2",
                ConfigurationFile = File.Exists(cfg) ? cfg : null,
                ShadowCopyFiles = "false"
            };

            Log.Info("[host] 创建 AppDomain：Base=" + setup.ApplicationBase + "，Config=" + (setup.ConfigurationFile ?? "<none>"));

            AppDomain domain = AppDomain.CreateDomain("PCL.Hosted", null, setup);
            int exitCode = 1;
            try
            {
                Log.Info("[host] 正在域内创建 DomainRunner（" + typeof(DomainRunner).Assembly.Location + "）…");
                DomainRunner runner = null;
                try
                {
                    runner = (DomainRunner)domain.CreateInstanceFromAndUnwrap(
                        typeof(DomainRunner).Assembly.Location,
                        typeof(DomainRunner).FullName);
                }
                catch (Exception exCreate)
                {
                    Log.Warn("[host] CreateInstanceFromAndUnwrap 失败，改用字节加载：" + exCreate.Message.Split('\n')[0]);
                }

                if (runner == null)
                {
                    // 单文件 / 网络盘场景：LoadFrom 会被 CAS 拒绝，直接按字节把自身加载进子域
                    byte[] self = File.ReadAllBytes(typeof(DomainRunner).Assembly.Location);
                    var childAsm = domain.Load(self);
                    var obj = childAsm.CreateInstance(typeof(DomainRunner).FullName);
                    runner = (DomainRunner)obj;
                    Log.Info("[host] 已通过字节加载创建 DomainRunner。");
                }
                exitCode = runner.Run(pclExe, opt.RulesPath, launcherDir,
                    opt.NoLoaderRestore, opt.NoUpdateBlock, opt.NoModpackDialog,
                    !opt.NoPatch, opt.OutDir ?? Path.Combine(runtimeDir, "reports"), Log.Path_);
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner is TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                Log.Error("[host] 运行失败", inner);
                throw;
            }
            finally
            {
                try { AppDomain.Unload(domain); Log.Info("[host] AppDomain 已卸载。"); }
                catch (Exception ex) { Log.Warn("[host] AppDomain 卸载失败：" + ex.Message); }
            }
            return exitCode;
        }
    }
}
