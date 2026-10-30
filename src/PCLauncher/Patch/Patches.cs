using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PCLauncher.Core;
using PCLauncher.Scan;

namespace PCLauncher.Patch
{
    /// <summary>
    /// 所有 Harmony 补丁体。Harmony 通过反射按名称调用这些静态方法。
    /// 配置在打补丁前由 PatchEngine 写入静态字段，补丁执行期间不再变化。
    /// 注意：Harmony 的 CodeInstruction.operand 是运行时成员（ConstructorInfo / MethodInfo / FieldInfo），
    /// 不是 Mono.Cecil 的引用类型。
    /// </summary>
    public static class Patches
    {
        public static string[] MarkerPrefixes = { "为遵守相关法律法规" };
        public static string HintTitle = "关闭下载";
        public static string ReplaceTitle = "Minecraft 版本";
        public static FieldInfo LoaderField;
        public static Type ModpackType;
        public static bool ModpackDialogEnabled = true;
        public static Type ModpackReturnType;

        // ---------------- 前缀 ----------------

        /// <summary>整个方法体作废，直接返回。</summary>
        public static bool SkipPrefix()
        {
            return false;
        }

        // ---------------- Transpiler ----------------

        /// <summary>
        /// 删除“ldstr 特征串 + newobj Exception(string) + throw”整段，
        /// 替换为 rethrow（该段位于 Catch 块尾部，语义等价于原始无参 Throw）。
        /// </summary>
        public static IEnumerable<CodeInstruction> MarkerThrowTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var result = new List<CodeInstruction>(list.Count);
            int i = 0;
            int replaced = 0;
            while (i < list.Count)
            {
                if (i + 2 < list.Count && IsMarkerLdstr(list[i]) && IsExceptionCtor(list[i + 1]) && list[i + 2].opcode == System.Reflection.Emit.OpCodes.Throw)
                {
                    var rethrow = new CodeInstruction(System.Reflection.Emit.OpCodes.Rethrow, null);
                    CopyLabels(list[i], rethrow);
                    CopyLabels(list[i + 1], rethrow);
                    CopyLabels(list[i + 2], rethrow);
                    rethrow.blocks.AddRange(list[i].blocks);
                    rethrow.blocks.AddRange(list[i + 1].blocks);
                    rethrow.blocks.AddRange(list[i + 2].blocks);
                    result.Add(rethrow);
                    i += 3;
                    replaced++;
                    continue;
                }
                result.Add(list[i]);
                i++;
            }
            if (replaced > 0) Log.Info("[transpiler] 已把 " + replaced + " 处下架 throw 段替换为 rethrow。");
            else Log.Warn("[transpiler] 未匹配到下架 throw 段（版本漂移？）。");
            return result;
        }

        /// <summary>
        /// 下载页加载器恢复：
        ///   1) 把 "关闭下载" 标题换成正常标题；
        ///   2) 把“构造伪 LoaderTask(null, state)”替换为读取真实 DlClientListLoader 字段。
        /// </summary>
        public static IEnumerable<CodeInstruction> LoaderTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var result = new List<CodeInstruction>(list.Count + 8);
            int hits = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var ci = list[i];
                bool replaced = false;

                if (ci.opcode == System.Reflection.Emit.OpCodes.Newobj && LoaderField != null)
                {
                    var ctor = ci.operand as ConstructorInfo;
                    if (ctor != null && ctor.DeclaringType != null &&
                        ctor.DeclaringType.Name.StartsWith("LoaderTask", StringComparison.Ordinal))
                    {
                        int argc = ctor.GetParameters().Length;
                        // 栈上原本是伪加载器的 N 个构造参数：全部弹出，再压入真实加载器字段。
                        for (int k = 0; k < argc; k++)
                        {
                            var pop = new CodeInstruction(System.Reflection.Emit.OpCodes.Pop, null);
                            if (k == 0) CopyLabels(ci, pop);
                            result.Add(pop);
                        }
                        var ldsfld = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldsfld, LoaderField);
                        if (argc == 0) CopyLabels(ci, ldsfld);
                        ldsfld.blocks.AddRange(ci.blocks);
                        result.Add(ldsfld);
                        hits++;
                        replaced = true;
                        Log.Info("[transpiler] 伪加载器 " + ctor.DeclaringType.Name + "(" + argc + " 参数) 已替换为 " + LoaderField.DeclaringType.Name + "." + LoaderField.Name);
                    }
                }

                if (!replaced) result.Add(ci);
            }

            if (hits == 0) Log.Warn("[transpiler] 未找到伪 LoaderTask 构造（版本漂移？），下载页可能仍显示为关闭状态。");
            return result;
        }

        // ---------------- 整合包手动安装入口 ----------------

        /// <summary>
        /// 恢复“手动安装整合包”：原方法体已被编译期折叠为“弹下架提示”，
        /// 这里改为弹出文件选择框，并调用仍然完整的 ModpackInstall(path, name, ...) 重载。
        /// </summary>
        public static bool ModpackInstallPrefix()
        {
            DoImportModpack();
            return false;
        }

        /// <summary>弹文件选择框 → 调用三参 ModpackInstall，供“导入整合包”等入口使用。</summary>
        public static void DoImportModpack()
        {
            if (!ModpackDialogEnabled || ModpackType == null) return;
            try
            {
                var overload = ModpackType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ModpackInstall" && m.GetParameters().Length == 3
                                         && m.GetParameters()[0].ParameterType == typeof(string));
                if (overload == null)
                {
                    Log.Warn("[modpack] 未找到三参数 ModpackInstall 重载，跳过。");
                    return;
                }

                string path = null;
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "选择要安装的整合包";
                    dlg.Filter = "整合包 (*.zip;*.mrpack;*.mcbbs)|*.zip;*.mrpack;*.mcbbs|所有文件 (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    path = dlg.FileName;
                }

                var ps = overload.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    args[i] = i == 0 ? path : (pt.IsValueType ? Activator.CreateInstance(pt) : null);
                }
                Log.Info("[modpack] 手动安装整合包：" + path);
                overload.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Log.Error("[modpack] 手动安装入口执行失败", ex);
            }
        }

        /// <summary>
        /// 恢复“从文件安装整合包”的真正实现：原方法体只保留了“打开压缩包并识别类型”，
        /// 之后的 Select Case 分发被编译期删除。这里整段接管，并复用 PCL 仍然完整的 InstallPack* 方法。
        /// </summary>
        public static bool ModpackInstallWithFilePrefix(string File, string InstanceName, string Logo, ref object __result)
        {
            if (!ModpackDialogEnabled || ModpackType == null) return true;
            __result = ModpackInstaller.Install(File, InstanceName, Logo, ModpackType);
            return false;
        }

        /// <summary>入口：把参数转交给运行时重建的分发器，返回值原样带回。</summary>
        public static object InstallObject(string File, string InstanceName, string Logo)
        {
            return ModpackInstaller.Install(File, InstanceName, Logo, ModpackType);
        }

        /// <summary>
        /// 整体替换 ModpackInstall(File, InstanceName, Logo) 的方法体：
        /// 原方法的分发逻辑在编译期被删除，这里改为调用 InstallObject，再 castclass 回原返回类型。
        /// </summary>
        public static IEnumerable<CodeInstruction> ModpackInstallReplacement(IEnumerable<CodeInstruction> instructions)
        {
            if (ModpackReturnType == null)
            {
                Log.Warn("[transpiler] 缺少 ModpackInstall 返回类型，放弃整体替换。");
                return instructions;
            }
            Log.Info("[transpiler] 整体替换 ModpackInstall 方法体 -> InstallObject() as " + ModpackReturnType.Name);
            return new List<CodeInstruction>
            {
                new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0, null),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_1, null),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_2, null),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Call, typeof(Patches).GetMethod("InstallObject")),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Castclass, ModpackReturnType),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Ret, null)
            };
        }

        /// <summary>诊断用：记录 PCL 权限检查抛出的真实异常（仅 --diag 时挂载）。</summary>
        public static void PermissionCheckFinalizer(Exception __exception)
        {
            if (__exception != null) Log.Error("[diag] PCL.CheckPermissionWithException 抛出", __exception);
            else Log.Info("[diag] PCL.CheckPermissionWithException 正常返回。");
        }

        // ---------------- 文件夹权限检查 ----------------

        /// <summary>
        /// 诊断：不改行为，只记录 PCL 自己的权限检查结果。
        /// </summary>
        public static bool CheckPermissionObservePrefix(string Folder)
        {
            bool ok = SafeCheck(Folder);
            Log.Info("[diag] PCL.CheckPermission(" + Folder + ") = " + ok);
            return true;
        }

        /// <summary>
        /// 兼容模式：PCL 的检查（写临时文件再删除）在部分环境会被 AV / 文件夹虚拟化 / 完整性标签拦截，
        /// 导致启动直接弹“运行环境错误”。这里用等效方式验证权限：能创建并写入探测文件（保留不删）。
        /// </summary>
        public static bool CheckPermissionWithExceptionPrefix(string Folder)
        {
            if (string.IsNullOrWhiteSpace(Folder))
                throw new ArgumentNullException("文件夹名不能为空！");

            string dir = Folder;
            if (!dir.EndsWith("\\", StringComparison.Ordinal) && !dir.EndsWith("/", StringComparison.Ordinal)) dir += "\\";
            if (dir.EndsWith(":\\System Volume Information\\", StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(":\\$RECYCLE.BIN\\", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("没有对系统文件夹的权限！");
            if (!System.IO.Directory.Exists(dir))
                throw new System.IO.DirectoryNotFoundException("文件夹不存在！");

            string probe = System.IO.Path.Combine(dir, "PCLMCFixer-perm-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try
            {
                try
                {
                    using (var f = new System.IO.FileStream(probe, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes("probing");
                        f.Write(bytes, 0, bytes.Length);
                    }
                }
                finally
                {
                    try { System.IO.File.Delete(probe); } catch (Exception) { }   // 不在用户目录里留垃圾
                }
                Log.Info("[perm] 权限探测通过：" + dir);
            }
            catch (Exception ex)
            {
                Log.Warn("[perm] 权限探测失败：" + dir + " -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            return false;
        }

        private static bool SafeCheck(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return false;
                string dir = folder.EndsWith("\\", StringComparison.Ordinal) ? folder : folder + "\\";
                if (!System.IO.Directory.Exists(dir)) return false;
                string probe = System.IO.Path.Combine(dir, "PCLMCFixer-diag-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                System.IO.File.WriteAllText(probe, "x");
                System.IO.File.Delete(probe);
                return true;
            }
            catch (Exception ex) { Log.Warn("[diag] 探测异常：" + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        public static System.Reflection.Assembly ResourceAssemblyOverride;

        /// <summary>
        /// PCL 启动时会 new SplashScreen(...)，WPF 用它自己的 Application.ResourceAssembly 找资源。
        /// 宿主进程的 EntryAssembly 是 PCLauncher，所以这里在真正构造 SplashScreen 的瞬间兜底设置一次。
        /// </summary>
        public static void SplashCtorPrefix()
        {
            Log.Info("[splash] SplashScreen 构造被调用。");
            try
            {
                var cur = System.Windows.Application.ResourceAssembly;
                Log.Info("[splash] 构造前 ResourceAssembly = " + (cur == null ? "<null>" : cur.GetName().Name));
                if (cur == null && ResourceAssemblyOverride != null)
                {
                    System.Windows.Application.ResourceAssembly = ResourceAssemblyOverride;
                    Log.Info("[splash] 已兜底设置 ResourceAssembly = " + ResourceAssemblyOverride.GetName().Name);
                }
            }
            catch (Exception ex) { Log.Warn("[splash] 兜底设置失败：" + ex.Message); }
        }

        public static void SplashCtorFinalizer(Exception __exception)
        {
            if (__exception != null) Log.Error("[splash] SplashScreen 构造异常（已跳过，不影响启动）", __exception);
        }

        /// <summary>
        /// PCL 启动时会给主窗口调 ChangeWindowMessageFilterEx（用于拖拽文件）。
        /// 在受限令牌 / AppContainer / 低完整性进程里这个调用会返回“拒绝访问”，
        /// 而 PCL 没有捕获，直接弹“程序出现未知错误”。这里只吞掉这一个 Win32 错误，
        /// 其它异常照常抛出，拖拽功能在正常环境里不受影响。
        /// </summary>
        public static Exception DragHelperFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            var w32 = __exception as System.ComponentModel.Win32Exception;
            if (w32 != null && w32.NativeErrorCode == 5)
            {
                Log.Warn("[compat] 忽略窗口消息过滤器设置失败（拒绝访问），拖拽文件功能在本环境不可用。");
                return null;
            }
            return __exception;
        }

        /// <summary>
        /// 恢复下载页左侧的“原版游戏”入口。
        /// 实测：PageDownloadLeft 里的 ItemInstall 与“社区资源”标题在发布版 XAML 里被折叠
        /// （UIA: offscreen=True、矩形 Infinity，且 ScrollViewer 不可滚动，说明不是滚出视口）。
        /// 这里在页面构造完成后把它们重新设为 Visible。
        /// </summary>
        public static void RestoreInstallEntryPostfix(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                int n = 0;
                foreach (var name in new[] { "_ItemInstall", "_LabResource", "ItemInstall", "LabResource" })
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f == null) continue;
                    var el = f.GetValue(__instance) as System.Windows.UIElement;
                    if (el == null) continue;
                    Log.Info("[ui] " + name + " 折叠状态：" + el.Visibility + " → Visible");
                    el.Visibility = System.Windows.Visibility.Visible;
                    n++;
                }
                Log.Info("[ui] 下载页原版入口恢复完成（" + n + " 个元素）。");
            }
            catch (Exception ex) { Log.Error("[ui] 恢复原版入口失败", ex); }
        }

        /// <summary>
        /// 本机连 HKCU\Software\PCL 都写不进去（受限令牌 / 沙箱注册表过滤）。
        /// PCL 的 Settings.Save 每写一次就抛异常并刷一条红条，这里直接让写入静默成功，
        /// 设置保持在内存中本次会话有效，不再打断使用。
        /// </summary>
        public static bool RegistryWritePrefix()
        {
            return false;
        }

        public static bool RegistryDeletePrefix()
        {
            return false;
        }

        /// <summary>
        /// 资源详情页里，整合包的“行点击”在发布版被折叠成“保存文件”（Save_Click），
        /// 真正的安装流程 Install_Click 仍在 exe 中。这里把“点整行”重定向到安装，
        /// 右侧那个“保存”小图标仍然走原来的保存逻辑。
        /// </summary>
        public static bool CompDetailSavePrefix(object __instance, object sender, EventArgs e)
        {
            try
            {
                if (SavingViaButton) return true;
                if (sender == null) return true;
                var st = sender.GetType();
                Log.Info("[modpack] Save_Click 触发：sender=" + st.FullName);

                object tag = null;
                var tagProp = st.GetProperty("Tag");
                if (tagProp != null) tag = tagProp.GetValue(sender, null);
                if (tag == null)
                {
                    var parentProp = st.GetProperty("Parent");
                    var parent = parentProp == null ? null : parentProp.GetValue(sender, null);
                    if (parent != null)
                    {
                        var pt2 = parent.GetType().GetProperty("Tag");
                        if (pt2 != null) tag = pt2.GetValue(parent, null);
                    }
                }
                if (tag == null) { Log.Info("[modpack] 无 Tag，走原逻辑"); return true; }

                object rt = null;
                var rtField = tag.GetType().GetField("ResourceType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rtField != null) rt = rtField.GetValue(tag);
                if (rt == null)
                {
                    var rtProp = tag.GetType().GetProperty("ResourceType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (rtProp != null) rt = rtProp.GetValue(tag, null);
                }
                Log.Info("[modpack] Tag=" + tag.GetType().Name + " ResourceType=" + (rt == null ? "<null>" : rt.ToString()));
                if (rt == null || rt.ToString() != "ModPack") return true;

                var install = __instance.GetType().GetMethod("Install_Click",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (install == null)
                {
                    Log.Warn("[modpack] 未找到 Install_Click，回退为保存文件。");
                    return true;
                }
                Log.Info("[modpack] 资源详情页：行点击 → 安装整合包");
                install.Invoke(__instance, new object[] { sender, e });
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("[modpack] 行点击转安装失败，回退保存", ex);
                return true;
            }
        }

        /// <summary>PCL 启动流程结束后再强制一次现代 TLS（防止它自己把协议改回弱版本）。</summary>
        public static void TlsPostfix()
        {
            try
            {
                var proto = System.Net.SecurityProtocolType.Tls12;
                try { proto |= (System.Net.SecurityProtocolType)12288; } catch (Exception) { }
                var old = System.Net.ServicePointManager.SecurityProtocol;
                System.Net.ServicePointManager.SecurityProtocol = proto;
                Log.Info("[net] Application_Startup 结束后 TLS：" + old + " → " + System.Net.ServicePointManager.SecurityProtocol);
            }
            catch (Exception ex) { Log.Warn("[net] 二次设置 TLS 失败：" + ex.Message); }
        }

        /// <summary>诊断：打印“Minecraft 文件夹列表”面板里的所有条目，确认“导入整合包”是否被创建。</summary>
        public static void McFolderListUiPostfix(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                // 注意：界面实际用的是静态单例 FrmSelectLeft，__instance 可能不是同一个对象
                object page = __instance;
                var sf = t.GetField("FrmSelectLeft", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (sf != null)
                {
                    var inst = sf.GetValue(null);
                    if (inst != null) page = inst;
                }
                object panel = null;
                var f = page.GetType().GetField("_PanList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) panel = f.GetValue(page);
                if (panel == null)
                {
                    var p = page.GetType().GetProperty("PanList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null) panel = p.GetValue(page, null);
                }
                if (panel == null) { Log.Warn("[ui] 未找到 PanList"); return; }
                var childrenObj = panel.GetType().GetProperty("Children").GetValue(panel, null);
                var children = childrenObj as System.Collections.IEnumerable;
                bool hasImport = false;
                int i = 0;
                foreach (var c in children)
                {
                    var ttl = c.GetType().GetProperty("Title");
                    if (ttl != null && Convert.ToString(ttl.GetValue(c, null)) == "导入整合包") hasImport = true;
                    string title = c.GetType().Name;
                    var tp = c.GetType().GetProperty("Title");
                    if (tp != null) title += " Title=" + tp.GetValue(c, null);
                    var tb = c.GetType().GetProperty("Text");
                    if (tb != null) title += " Text=" + tb.GetValue(c, null);
                    Log.Info("[ui] 文件夹面板[" + (i++) + "] " + title);
                }
                if (!hasImport) InjectImportItem(childrenObj, panel);
            }
            catch (Exception ex) { Log.Warn("[ui] 打印文件夹面板失败：" + ex.Message); }
        }

        /// <summary>面板里没有“导入整合包”时，自己补一个（调用与官方相同的 ModpackInstall）。</summary>
        private static void InjectImportItem(object childrenObj, object panel)
        {
            try
            {
                var children = childrenObj as System.Collections.IList;
                if (children == null) { Log.Warn("[ui] Children 不是 IList，无法注入"); return; }
                var asm = panel.GetType().Assembly;
                if (asm.GetType("PCL.MyListItem") == null) asm = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("PCL.MyListItem")).FirstOrDefault(x => x != null) == null ? asm : asm;
                var itemType = asm.GetType("PCL.MyListItem") ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("PCL.MyListItem")).FirstOrDefault(x => x != null);
                if (itemType == null) { Log.Warn("[ui] 未找到 PCL.MyListItem"); return; }
                var item = Activator.CreateInstance(itemType);
                void Set(string name, object v)
                {
                    var p = itemType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanWrite) p.SetValue(item, v, null);
                }
                Set("IsScaleAnimationEnabled", false);
                Set("Type", 1);                 // MyListItem.CheckType.Clickable
                Set("Title", "导入整合包");
                Set("Height", 34.0);
                Set("ToolTip", "在当前选择的 Minecraft 文件夹下安装整合包");
                var ev = itemType.GetEvent("Click");
                if (ev != null)
                {
                    var handler = Delegate.CreateDelegate(ev.EventHandlerType, typeof(Patches).GetMethod("ImportModpackClick"));
                    ev.AddEventHandler(item, handler);
                }
                children.Add(item);
                Log.Warn("[ui] 面板缺少“导入整合包”，已注入一项（点击后弹出文件选择）。");
            }
            catch (Exception ex) { Log.Error("[ui] 注入“导入整合包”失败", ex); }
        }

        public static void ImportModpackClick(object sender, EventArgs e)
        {
            DoImportModpack();
        }

        private static object UnwrapVirtualizing(object wrapper, out Type itemType)
        {
            itemType = null;
            var t = wrapper.GetType();
            foreach (var name in new[] { "Element", "Item", "Content", "Child", "Inner", "Target" })
            {
                var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var v = prop.GetValue(wrapper, null);
                    if (v != null && v.GetType().Name == "MyListItem") { itemType = v.GetType(); return v; }
                }
                var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var v = field.GetValue(wrapper);
                    if (v != null && v.GetType().Name == "MyListItem") { itemType = v.GetType(); return v; }
                }
            }
            return null;
        }

        [ThreadStatic] private static bool SavingViaButton;

        /// <summary>给资源列表项补回被折叠的“另存为”按钮（发布版因为没有传保存回调而省略了它）。</summary>
        private static bool _loggedItemType;

        public static void ResourceItemPostfix(object __result)
        {
            try
            {
                if (__result == null) return;
                var outerType = __result.GetType();
                if (!_loggedItemType)
                {
                    _loggedItemType = true;
                    Log.Info("[ui] ToListItem 返回类型：" + outerType.FullName);
                }

                object item = __result;
                var itemType = item.GetType();
                if (itemType.Name != "MyListItem")
                {
                    // 发布版返回 MyVirtualizingElement(Of MyListItem)：尝试取出里面真正的 MyListItem
                    item = UnwrapVirtualizing(__result, out itemType);
                    if (item == null) { Log.Warn("[ui] 无法从 " + outerType.Name + " 取出 MyListItem，跳过另存为按钮"); return; }
                }
                __result = item;
                var buttonsProp = itemType.GetProperty("Buttons", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (buttonsProp == null) return;
                var cur = buttonsProp.GetValue(__result, null) as Array;
                if (cur != null && cur.Length > 0) return;

                var asm = itemType.Assembly;
                var btnType = asm.GetType("PCL.MyIconButton");
                if (btnType == null) return;
                var btn = Activator.CreateInstance(btnType);
                var logo = btnType.GetProperty("Logo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (logo != null) logo.SetValue(btn, SaveIconPath, null);
                var tt = btnType.GetProperty("ToolTip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         ?? btnType.GetProperty("ToolTip", BindingFlags.Instance | BindingFlags.Public);
                var fwTip = typeof(System.Windows.FrameworkElement).GetProperty("ToolTip");
                if (fwTip != null) fwTip.SetValue(btn, "另存为", null);
                var tagProp = btnType.GetProperty("Tag", BindingFlags.Instance | BindingFlags.Public);
                if (tagProp != null) tagProp.SetValue(btn, __result, null);

                var ev = btnType.GetEvent("Click");
                if (ev != null)
                {
                    var handler = Delegate.CreateDelegate(ev.EventHandlerType, typeof(Patches).GetMethod("SaveButtonClick"));
                    ev.AddEventHandler(btn, handler);
                }
                var arr = Array.CreateInstance(btnType, 1);
                arr.SetValue(btn, 0);
                buttonsProp.SetValue(__result, arr, null);
            }
            catch (Exception ex) { Log.Warn("[ui] 补“另存为”按钮失败：" + ex.Message); }
        }

        public const string SaveIconPath = "M819.392 0L1024 202.752v652.16a168.96 168.96 0 0 1-168.832 168.768h-104.192a47.296 47.296 0 0 1-10.752 0H283.776a47.232 47.232 0 0 1-10.752 0H168.832A168.96 168.96 0 0 1 0 854.912V202.752L204.608 0h614.784zM204.608 102.4L102.4 203.776v651.136a67.072 67.072 0 0 0 66.432 67.2h686.336a67.072 67.072 0 0 0 66.432-67.2V203.776L819.392 102.4H204.608z";

        public static void SaveButtonClick(object sender, EventArgs e)
        {
            try
            {
                var item = sender.GetType().GetProperty("Tag")?.GetValue(sender, null);
                if (item == null) return;
                var asmPcl = item.GetType().Assembly;
                var frmMain = asmPcl.GetType("PCL.FormMain") ?? asmPcl.GetType("PCL.FrmMain");
                var pageProp = frmMain?.GetProperty("PageRight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var page = pageProp?.GetValue(null, null);
                if (page == null) { Log.Warn("[ui] 未取到当前页面，无法保存"); return; }
                var save = page.GetType().GetMethod("Save_Click", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (save == null) { Log.Warn("[ui] 未找到 Save_Click"); return; }
                SavingViaButton = true;
                try { save.Invoke(page, new object[] { item, e }); }
                finally { SavingViaButton = false; }
            }
            catch (Exception ex) { Log.Error("[ui] 另存为执行失败", ex); }
        }

        /// <summary>
        /// PCL 所有文件操作都经过 MeloongCore.PathUtils.ForApi 转成 \\?\ 长路径。
        /// 单文件发布没有 exe.config，旧式路径处理下这种路径会抛 ArgumentException（表现为“没有对当前文件夹的权限”）。
        /// 这里把前缀去掉，用普通路径，行为等价（只是不支持 >260 字符的超长路径）。
        /// </summary>
        public static void ForApiPostfix(ref string __result)
        {
            if (__result == null) return;
            if (__result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                __result = @"\\" + __result.Substring(8);          // \\?\UNC\srv\share -> \\srv\share
            else if (__result.StartsWith(@"\\?\", StringComparison.Ordinal))
                __result = __result.Substring(4);
        }

        // ---------------- 工具 ----------------

        private static bool IsMarkerLdstr(CodeInstruction ci)
        {
            if (ci.opcode != System.Reflection.Emit.OpCodes.Ldstr) return false;
            var s = ci.operand as string;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var p in MarkerPrefixes)
                if (!string.IsNullOrEmpty(p) && s.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsExceptionCtor(CodeInstruction ci)
        {
            if (ci.opcode != System.Reflection.Emit.OpCodes.Newobj) return false;
            var ctor = ci.operand as ConstructorInfo;
            if (ctor == null) return false;
            var ps = ctor.GetParameters();
            return ctor.DeclaringType.FullName == "System.Exception"
                   && ps.Length == 1 && ps[0].ParameterType == typeof(string);
        }

        private static void CopyLabels(CodeInstruction from, CodeInstruction to)
        {
            if (from.labels != null && from.labels.Count > 0)
            {
                to.labels.AddRange(from.labels);
                from.labels.Clear();
            }
        }
    }
}
