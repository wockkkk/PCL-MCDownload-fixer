using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Resources;
using PCLauncher.Core;

namespace PCLauncher.Host
{
    /// <summary>
    /// PCL 把部分依赖（MeloongCore 等）以资源形式内嵌在 exe 中，并在自己的启动流程里注册 AssemblyResolve。
    /// 我们的补丁必须早于 PCL 启动，所以先自己把内嵌依赖预载进域，保证 Harmony 解析方法签名时不缺程序集。
    /// </summary>
    public static class DependencyResolver
    {
        private static readonly Dictionary<string, Assembly> Cache =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这些内嵌程序集不能由宿主预载，必须留给 PCL 自己的 AssemblyResolve 回调去加载。
        /// 目前只有 Imazen.WebP：PCL 在加载它之后会紧接着调用 ExtractLibwebp()，
        /// 把内嵌的 libwebp64 释放成 &lt;PCL 工作目录&gt;\libwebp.dll 并 SetDllDirectory。
        /// 宿主若抢先预载，CLR 按名称直接命中已加载程序集，PCL 的回调不会触发，
        /// 原生 libwebp 永远不会就位——表现就是“加载版本图标失败：DllNotFoundException libwebp”。
        /// </summary>
        private static readonly HashSet<string> DeferredToPcl =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Imazen.WebP" };

        public static void Preload(Assembly pcl)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in pcl.GetReferencedAssemblies()) referenced.Add(r.Name);

            foreach (var resName in pcl.GetManifestResourceNames())
            {
                if (!resName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using (var s = pcl.GetManifestResourceStream(resName))
                    using (var rr = new ResourceReader(s))
                    {
                        var e = rr.GetEnumerator();
                        while (e.MoveNext())
                        {
                            var key = e.Key as string;
                            var val = e.Value;
                            var bytes = val as byte[];
                            if (bytes == null || bytes.Length < 2) continue;
                            // 不再只加载 PCL 的直接引用：MeloongCore 还依赖 Microsoft.Win32.Registry 等间接程序集
                            if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                            {
                                Log.Info("[deps] 跳过资源 " + resName + " :: " + key + " (" + bytes.Length + " 字节，非 PE)");
                                continue;
                            }
                            if (key != null && DeferredToPcl.Contains(key))
                            {
                                Log.Info("[deps] 延迟交由 PCL 自行加载 " + key + "（它需要先释放原生依赖 libwebp）");
                                continue;
                            }
                            var pe = PeReader.TryParse(bytes, bytes.Length);
                            if (pe != null && !pe.IsManaged)
                            {
                                // 原生 DLL（如 libwebp64）：PCL 自己会在启动时释放并 LoadLibrary，这里不能也不能 Assembly.Load
                                Log.Info("[deps] 跳过内嵌原生库 " + key + "（" + pe + "，" + bytes.Length + " 字节）");
                                continue;
                            }
                            try
                            {
                                var a = Assembly.Load(bytes);
                                Cache[a.GetName().Name] = a;
                                Log.Info("[deps] 预载内嵌程序集 " + a.GetName().Name + " " + a.GetName().Version + "（资源 " + key + "，" + bytes.Length + " 字节）");
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("[deps] 加载内嵌 " + key + " 失败：" + ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[deps] 读取资源 " + resName + " 失败：" + ex.Message);
                }
            }

            if (Cache.Count == 0) Log.Warn("[deps] 未从内嵌资源中找到依赖程序集。");
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            Assembly a;
            var name = new AssemblyName(args.Name).Name;
            if (Cache.TryGetValue(name, out a)) return a;
            return null;
        }
    }
}
