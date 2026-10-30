using System;
using System.IO;
using System.Reflection;

namespace PCLauncher.Core
{
    /// <summary>
    /// 单文件发布支持：0Harmony / Mono.Cecil 以嵌入资源形式放在 exe 里，
    /// 运行时通过 AssemblyResolve 从自身资源加载，不需要旁边放 dll。
    /// </summary>
    public static class Embedded
    {
        private static readonly Assembly Self = typeof(Embedded).Assembly;
        private static bool _hooked;
        private static readonly System.Collections.Generic.Dictionary<string, Assembly> Cache =
            new System.Collections.Generic.Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                Assembly cached;
                if (Cache.TryGetValue(name, out cached)) return cached;   // 必须复用同一实例，否则 Cecil 会加载出两份导致类型标识不一致
                using (var s = Self.GetManifestResourceStream(name + ".dll"))
                {
                    if (s == null) return null;
                    var buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = s.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != buf.Length) Array.Resize(ref buf, read);
                    Log.Info("[loader] 从内嵌资源加载 " + name + "（" + buf.Length + " 字节）");
                    var loaded = Assembly.Load(buf);
                    Cache[name] = loaded;
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[loader] 内嵌加载 " + args.Name + " 失败：" + ex.Message);
                return null;
            }
        }

        public static bool Has(string assemblySimpleName)
        {
            return Self.GetManifestResourceStream(assemblySimpleName + ".dll") != null;
        }

        /// <summary>进程内尽量打开长路径与强加密（子域另有 config 兜底）。</summary>
        public static void ApplySwitches()
        {
            TrySwitch("Switch.System.IO.UseLegacyPathHandling", false);
            TrySwitch("Switch.System.IO.BlockLongPaths", false);
            TrySwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
            TrySwitch("Switch.System.Net.DontEnableTls13", false);
        }

        private static void TrySwitch(string name, bool value)
        {
            try { AppContext.SetSwitch(name, value); } catch (Exception) { }
        }
    }
}
