using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PCLauncher.Core;
using PCLauncher.Scan;

namespace PCLauncher.Patch
{
    public sealed class PatchResult
    {
        public readonly List<string> Applied = new List<string>();
        public readonly List<string> Failed = new List<string>();
        public readonly List<string> Skipped = new List<string>();
    }

    public static class PatchEngine
    {
        public const string HarmonyId = "PCL.PCLauncher.Recovery";

        public static PatchResult Apply(Assembly pcl, ScanReport report, Rules rules, bool enablePatch)
        {
            var result = new PatchResult();
            var harmony = new Harmony(HarmonyId);

            Patches.ModpackDialogEnabled = rules.ModpackDialog.Enabled;

            var module = pcl.ManifestModule;

            if (!enablePatch)
            {
                Log.Warn("已指定 --no-patch：跳过全部补丁。");
                result.Skipped.Add("全部（--no-patch）");
                return result;
            }

            Patches.ModpackType = pcl.GetType(rules.ModpackDialog.Type, false, true);

            var skipPrefix = new HarmonyMethod(typeof(Patches).GetMethod("SkipPrefix"));

            // ---- 1. 特征串拦截点 ----
            foreach (var site in report.Sites)
            {
                string id = site.DeclaringType + "::" + site.MethodName + " (0x" + site.Token.ToString("X8") + ")";
                if (site.Kind == SiteKind.CompareTrap)
                {
                    result.Skipped.Add(id + " —— 真实运行时字符串比较，不处理");
                    Log.Info("[patch] 跳过陷阱点 " + id);
                    continue;
                }
                if (site.Kind == SiteKind.Unknown)
                {
                    result.Failed.Add(id + " —— 未识别形态");
                    Log.Warn("[patch] 未识别的拦截点 " + id);
                    continue;
                }

                try
                {
                    var mb = module.ResolveMethod(site.Token);
                    bool isModpackInstall = site.DeclaringType == rules.ModpackDialog.Type
                                            && site.MethodName == rules.ModpackDialog.MethodName;
                    if (site.Kind == SiteKind.InlineThrow && isModpackInstall && rules.ModpackDialog.Enabled && mb.GetParameters().Length == 3)
                    {
                        Patches.ModpackReturnType = mb is MethodInfo ? ((MethodInfo)mb).ReturnType : null;
                        harmony.Patch(mb, transpiler: new HarmonyMethod(typeof(Patches).GetMethod("ModpackInstallReplacement")));
                    }
                    else if (site.Kind == SiteKind.InlineThrow)
                    {
                        // 该 throw 位于 try/finally（无 catch）中，rethrow 会生成非法 IL；
                        // 宁可不动，也不打一个会让方法失效的补丁。
                        result.Skipped.Add(id + " —— 内联 throw 无 catch 上下文，跳过（未修改）");
                        Log.Warn("[patch] 跳过内联 throw（无 catch 上下文）：" + id);
                        continue;    // 不要继续落到下面的 Applied.Add，否则同一条会既算跳过又算成功
                    }
                    else if (site.Kind == SiteKind.Hint && isModpackInstall && rules.ModpackDialog.Enabled && mb.GetParameters().Length == 0)
                    {
                        harmony.Patch(mb, prefix: new HarmonyMethod(typeof(Patches).GetMethod("ModpackInstallPrefix")));
                    }
                    else
                    {
                        harmony.Patch(mb, prefix: skipPrefix);
                    }
                    result.Applied.Add(id + " —— " + site.KindText);
                    Log.Info("[patch] 已打补丁 " + id + " [" + site.KindText + "]");
                }
                catch (Exception ex)
                {
                    result.Failed.Add(id + " —— " + ex.Message);
                    Log.Error("[patch] 打补丁失败 " + id, ex);
                }
            }

            // ---- 2. 下载页加载器 ----
            if (rules.Loader.Mode != "none" && report.Loader.Present)
            {
                string id = report.Loader.DeclaringType + "::" + report.Loader.MethodName + " (loader)";
                try
                {
                    var mb = module.ResolveMethod(report.Loader.Token);
                    bool useReplace = rules.Loader.Mode == "replace" && report.Loader.PatternMatched;
                    if (useReplace)
                    {
                        var fieldType = pcl.GetType(rules.Loader.LoaderFieldType, false, true);
                        Patches.LoaderField = fieldType != null
                            ? fieldType.GetField(rules.Loader.LoaderFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                            : null;
                        if (Patches.LoaderField == null)
                        {
                            Log.Warn("[patch] 未找到加载器字段 " + report.Loader.LoaderField + "，回退为跳过策略。");
                            harmony.Patch(mb, prefix: skipPrefix);
                            result.Applied.Add(id + " —— 回退：prefix-skip");
                        }
                        else
                        {
                            harmony.Patch(mb, transpiler: new HarmonyMethod(typeof(Patches).GetMethod("LoaderTranspiler")));
                            result.Applied.Add(id + " —— transpiler 替换为真实加载器");
                            Log.Info("[patch] 下载页加载器已替换为 " + report.Loader.LoaderField);
                        }
                    }
                    else
                    {
                        harmony.Patch(mb, prefix: skipPrefix);
                        result.Applied.Add(id + " —— prefix-skip（跳过伪加载器装配）");
                        Log.Info("[patch] 下载页 LoaderInit 已跳过。");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed.Add(id + " —— " + ex.Message);
                    Log.Error("[patch] 加载器补丁失败", ex);
                }
            }

            // ---- 3. 更新禁用 ----
            PCLauncher.Update.UpdateBlocker.Apply(harmony, pcl, report, rules, result.Applied, result.Failed);

            // ---- 4. 整合包类型引用（供补丁体使用） ----
            if (rules.ModpackDialog.Enabled)
            {
                Patches.ModpackType = pcl.GetType(rules.ModpackDialog.Type, false, true);
                if (Patches.ModpackType == null) Log.Warn("[patch] 未找到 " + rules.ModpackDialog.Type + "，整合包恢复不可用。");
                else
                {
                    var installers = Patches.ModpackType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .Where(m => m.Name.StartsWith("InstallPack", StringComparison.Ordinal))
                        .Select(m => m.Name + "/" + m.GetParameters().Length).OrderBy(x => x).ToArray();
                    Log.Info("[patch] 整合包分发目标：" + string.Join(", ", installers));
                }
            }

            // ---- 4.3 文件夹权限检查兼容（解决“PCL 没有对当前文件夹的权限”） ----
            if (Environment.GetEnvironmentVariable("PCLAUNCHER_PERM_COMPAT") == "1" && rules.PermissionCompat)
            {
                try
                {
                    var modBase = pcl.GetType("PCL.ModBase", false, true);
                    if (modBase != null)
                    {
                        var withEx = modBase.GetMethod("CheckPermissionWithException", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (withEx != null)
                        {
                            harmony.Patch(withEx, prefix: new HarmonyMethod(typeof(Patches).GetMethod("CheckPermissionWithExceptionPrefix")));
                            result.Applied.Add("PCL.ModBase::CheckPermissionWithException —— 权限检查兼容");
                            Log.Info("[patch] 已启用权限检查兼容。");
                        }
                        var plain = modBase.GetMethod("CheckPermission", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (plain != null)
                            harmony.Patch(plain, prefix: new HarmonyMethod(typeof(Patches).GetMethod("CheckPermissionObservePrefix")));
                    }
                    else Log.Warn("[patch] 未找到 PCL.ModBase，权限兼容未启用。");
                }
                catch (Exception ex) { result.Failed.Add("CheckPermission —— " + ex.Message); Log.Error("[patch] 权限兼容失败", ex); }
            }

            // ---- 4.5 诊断：权限检查异常 ----
            if (Environment.GetEnvironmentVariable("PCLAUNCHER_DIAG") == "1")
            {
                try
                {
                    var modBase = pcl.GetType("PCL.ModBase", false, true);
                    var perm = modBase == null ? null : modBase.GetMethod("CheckPermissionWithException",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (perm != null)
                    {
                        harmony.Patch(perm, finalizer: new HarmonyMethod(typeof(Patches).GetMethod("PermissionCheckFinalizer")));
                        Log.Info("[diag] 已挂载 CheckPermissionWithException 诊断。");
                    }
                    else Log.Warn("[diag] 未找到 CheckPermissionWithException。");
                }
                catch (Exception ex) { Log.Warn("[diag] 挂载诊断失败：" + ex.Message); }
            }

            // ---- 4.8 WPF SplashScreen 资源兜底 ----
            try
            {
                Patches.ResourceAssemblyOverride = pcl;
                var splash = typeof(System.Windows.SplashScreen);
                Log.Info("[patch] SplashScreen 类型 = " + (splash == null ? "<null>" : splash.AssemblyQualifiedName));
                if (splash != null)
                {
                    var ctor = splash.GetConstructor(new[] { typeof(string) });
                    Log.Info("[patch] SplashScreen(string) 构造 = " + (ctor == null ? "<null>" : "ok"));
                    if (ctor != null)
                    {
                        harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(Patches).GetMethod("SplashCtorPrefix")));
                        Log.Info("[patch] 已挂载 SplashScreen 资源兜底。");
                    }

                    // 宿主环境里 WPF SplashScreen 依赖 EntryAssembly 的资源查找，会抛 ArgumentNullException(resourceAssembly)。
                    // 启动画面只是装饰：直接跳过构造 + Show/Close，让 PCL 正常进入主界面。
                    if (rules.DisableSplash)
                    {
                        if (ctor != null)
                            harmony.Patch(ctor, prefix: skipPrefix, finalizer: new HarmonyMethod(typeof(Patches).GetMethod("SplashCtorFinalizer")));
                        int skipped = 0;
                        foreach (var m in splash.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if ((m.Name == "Show" || m.Name == "Close") && m.ReturnType == typeof(void) && !m.IsAbstract)
                            {
                                harmony.Patch(m, prefix: skipPrefix);
                                skipped++;
                            }
                        }
                        result.Applied.Add("System.Windows.SplashScreen —— 跳过启动画面（宿主兼容）");
                        Log.Info("[patch] 已禁用启动画面（Show/Close 共 " + skipped + " 个方法）。");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("[patch] SplashScreen 兜底失败：" + ex.Message); }

            // ---- 4.9 受限环境兼容：吞掉拖拽钩子的“拒绝访问” ----
            if (rules.EnvironmentCompat)
            {
                try
                {
                    var drag = pcl.GetType("PCL.DragHelper", false, true);
                    if (drag != null)
                    {
                        int n = 0;
                        foreach (var m in drag.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        {
                            if (m.Name != "ChangeMessageFilter" && m.Name != "AddHook") continue;
                            if (m.GetMethodBody() == null) continue;
                            harmony.Patch(m, finalizer: new HarmonyMethod(typeof(Patches).GetMethod("DragHelperFinalizer")));
                            n++;
                        }
                        Log.Info("[patch] 已对 DragHelper 挂载兼容处理（" + n + " 个方法）。");
                    }
                }
                catch (Exception ex) { Log.Warn("[patch] DragHelper 兼容失败：" + ex.Message); }
            }

            // ---- 4.95 下载页“原版游戏”入口 ----
            if (rules.RestoreInstallEntry)
            {
                try
                {
                    var left = pcl.GetType("PCL.PageDownloadLeft", false, true);
                    var ctor = left == null ? null : left.GetConstructor(Type.EmptyTypes);
                    if (ctor != null)
                    {
                        harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(Patches).GetMethod("RestoreInstallEntryPostfix")));
                        result.Applied.Add("PCL.PageDownloadLeft —— 恢复“原版游戏”入口");
                        Log.Info("[patch] 已挂载下载页“原版游戏”入口恢复。");
                    }
                    else Log.Warn("[patch] 未找到 PageDownloadLeft 构造。");
                }
                catch (Exception ex) { Log.Warn("[patch] 原版入口恢复挂载失败：" + ex.Message); }
            }

            // ---- 4.97 注册表不可写兼容 ----
            if (rules.RegistryCompat || ProbeRegistryWritable() == false)
            {
                try
                {
                    var ru = pcl.GetType("MeloongCore.RegistryUtils", false, true)
                             ?? Type.GetType("MeloongCore.RegistryUtils, MeloongCore", false);
                    if (ru != null)
                    {
                        var w = ru.GetMethod("Write", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var d = ru.GetMethod("Delete", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (w != null) harmony.Patch(w, prefix: new HarmonyMethod(typeof(Patches).GetMethod("RegistryWritePrefix")));
                        if (d != null) harmony.Patch(d, prefix: new HarmonyMethod(typeof(Patches).GetMethod("RegistryDeletePrefix")));
                        result.Applied.Add("MeloongCore.RegistryUtils —— 注册表写入静默（本机不可写）");
                        Log.Warn("[patch] 本机注册表不可写，已启用 registryCompat：设置仅本次会话生效，不再刷红条。");
                    }
                    else Log.Warn("[patch] 未找到 MeloongCore.RegistryUtils。");
                }
                catch (Exception ex) { Log.Warn("[patch] registryCompat 失败：" + ex.Message); }
            }

            // ---- 4.99 资源详情页：整合包行点击 → 安装 ----
            if (rules.RestoreModpackRowClick)
            {
                try
                {
                    var cd = pcl.GetType("PCL.PageDownloadResourceDetail", false, true) ?? pcl.GetType("PCL.PageDownloadCompDetail", false, true);
                    var save = cd == null ? null : cd.GetMethod("Save_Click", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (save != null)
                    {
                        harmony.Patch(save, prefix: new HarmonyMethod(typeof(Patches).GetMethod("CompDetailSavePrefix")));
                        result.Applied.Add("PCL.PageDownloadCompDetail::Save_Click —— 整合包行点击改为安装");
                        Log.Info("[patch] 已把整合包列表的行点击重定向为“安装”。");
                    }
                    else Log.Warn("[patch] 未找到 PageDownloadCompDetail.Save_Click。");
                }
                catch (Exception ex) { Log.Warn("[patch] 整合包行点击补丁失败：" + ex.Message); }
            }

            // ---- 4.98 TLS 兜底 ----
            try
            {
                var appType = pcl.GetType("PCL.Application", false, true);
                var startup = appType == null ? null : appType.GetMethod("Application_Startup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (startup != null)
                {
                    harmony.Patch(startup, postfix: new HarmonyMethod(typeof(Patches).GetMethod("TlsPostfix")));
                    Log.Info("[patch] 已挂载 TLS 启动后兜底。");
                }
            }
            catch (Exception ex) { Log.Warn("[patch] TLS 兜底挂载失败：" + ex.Message); }

            // ---- 4.96 文件夹面板诊断 ----
            try
            {
                var sel = pcl.GetType("PCL.PageSelectLeft", false, true);
                var m = sel == null ? null : sel.GetMethod("McFolderListUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patches).GetMethod("McFolderListUiPostfix")));
                    Log.Info("[patch] 已挂载文件夹面板诊断。");
                }
                else Log.Warn("[patch] 未找到 PageSelectLeft.McFolderListUI。");
            }
            catch (Exception ex) { Log.Warn("[patch] 文件夹面板诊断挂载失败：" + ex.Message); }

            // ---- 4.94 资源列表项补回“另存为”按钮 ----
            if (rules.RestoreSaveButton)
            {
                try
                {
                    var rv = pcl.GetType("PCL.ResourceVersion", false, true);
                    var toItem = rv == null ? null : rv.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "ToListItem");
                    if (toItem != null)
                    {
                        harmony.Patch(toItem, postfix: new HarmonyMethod(typeof(Patches).GetMethod("ResourceItemPostfix")));
                        result.Applied.Add("PCL.ResourceVersion::ToListItem —— 恢复“另存为”按钮");
                        Log.Info("[patch] 已为资源列表项恢复“另存为”按钮。");
                    }
                    else Log.Warn("[patch] 未找到 ResourceVersion.ToListItem。");
                }
                catch (Exception ex) { Log.Warn("[patch] 另存为按钮补丁失败：" + ex.Message); }
            }

            // ---- 4.92 长路径兼容（去掉 \\?\ 前缀） ----
            if (rules.LongPathCompat)
            {
                try
                {
                    var pu = pcl.GetType("MeloongCore.PathUtils", false, true)
                             ?? Type.GetType("MeloongCore.PathUtils, MeloongCore", false);
                    var forApi = pu == null ? null : pu.GetMethod("ForApi", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (forApi != null)
                    {
                        harmony.Patch(forApi, postfix: new HarmonyMethod(typeof(Patches).GetMethod("ForApiPostfix")));
                        result.Applied.Add("MeloongCore.PathUtils::ForApi —— 去掉长路径前缀");
                        Log.Info("[patch] 已应用长路径兼容（ForApi → 普通路径）。");
                    }
                    else Log.Warn("[patch] 未找到 PathUtils.ForApi。");
                }
                catch (Exception ex) { Log.Warn("[patch] 长路径兼容失败：" + ex.Message); }
            }

            // ---- 5. JIT 校验：强制编译补丁后的方法，确认 IL 合法 ----
            VerifyJit(pcl, report, result);

            Log.Info(string.Format("[patch] 汇总：成功 {0}，失败 {1}，跳过 {2}。", result.Applied.Count, result.Failed.Count, result.Skipped.Count));
            return result;
        }

        /// <summary>探测 HKCU\Software\PCL 是否可写。</summary>
        private static bool ProbeRegistryWritable()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\PCL", true))
                {
                    if (k == null) return false;
                    k.SetValue("__PCLMCFixerProbe__", "1");
                    k.DeleteValue("__PCLMCFixerProbe__", false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[patch] 注册表探测失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>强制 JIT 编译补丁后的目标方法，验证改写出的 IL 合法（不会执行方法体）。</summary>
        private static void VerifyJit(Assembly pcl, ScanReport report, PatchResult result)
        {
            var tokens = new List<int>();
            foreach (var s in report.Sites)
                if (s.Kind != SiteKind.CompareTrap && s.Kind != SiteKind.Unknown) tokens.Add(s.Token);
            if (report.Loader.Present && report.Loader.PatternMatched) tokens.Add(report.Loader.Token);
            foreach (var u in report.UpdateTargets) tokens.Add(u.Token);

            int ok = 0;
            foreach (var token in tokens)
            {
                try
                {
                    var mb = pcl.ManifestModule.ResolveMethod(token);
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(mb.MethodHandle);
                    ok++;
                }
                catch (Exception ex)
                {
                    result.Failed.Add("JIT 校验失败 token=0x" + token.ToString("X8") + " —— " + ex.GetType().Name + ": " + ex.Message);
                    Log.Error("[verify] JIT 校验失败 token=0x" + token.ToString("X8"), ex);
                }
            }
            Log.Info("[verify] JIT 校验通过 " + ok + "/" + tokens.Count + " 个目标方法。");
        }
    }
}
