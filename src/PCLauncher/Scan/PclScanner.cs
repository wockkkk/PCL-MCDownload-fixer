using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PCLauncher.Core;

namespace PCLauncher.Scan
{
    public static class PclScanner
    {
        public static ScanReport Scan(string path, Rules rules)
        {
            var report = new ScanReport();
            report.FilePath = System.IO.Path.GetFullPath(path);
            report.FileSize = new System.IO.FileInfo(path).Length;
            report.RulesSource = rules.Source;
            report.Sha256 = Crypto.Sha256File(path);

            var rp = new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                ReadingMode = ReadingMode.Immediate
            };

            using (var asm = AssemblyDefinition.ReadAssembly(path, rp))
            {
                report.AssemblyName = asm.Name.Name;
                report.AssemblyVersion = asm.Name.Version.ToString();
                report.RuntimeVersion = asm.MainModule.Runtime.ToString();
                report.Architecture = asm.MainModule.Architecture.ToString();
                report.EntryPoint = asm.EntryPoint != null ? asm.EntryPoint.FullName : "<none>";
                report.TypeCount = CountTypes(asm.MainModule);
                report.MethodCount = asm.MainModule.Types.Sum(t => CountMethods(t));
                report.FieldCount = asm.MainModule.Types.Sum(t => CountFields(t));

                foreach (var method in AllMethods(asm.MainModule))
                {
                    if (!method.HasBody) continue;
                    ScanMethod(method, rules, report);
                }

                ScanLoader(asm.MainModule, rules, report);
                ScanUpdates(asm.MainModule, rules, report);
            }

            if (report.Sites.Count == 0)
                report.Warnings.Add("未发现任何特征字符串引用：可能是限制上线前的旧版本（如 2.13.x，属正常），也可能是拦截形态已改变，请结合版本号判断。");

            foreach (var s in report.Sites.Where(s => s.Kind == SiteKind.Unknown))
                report.Warnings.Add("存在未识别的拦截点：" + s.DeclaringType + "::" + s.MethodName + " 窗口：" + s.Window);

            if (report.Loader.Present && !report.Loader.PatternMatched && rules.Loader.Mode == "replace")
                report.Warnings.Add("下载页加载器替换模式未匹配到预期序列，将回退为跳过策略。");

            return report;
        }

        private static void ScanMethod(MethodDefinition method, Rules rules, ScanReport report)
        {
            var body = method.Body;
            if (body == null || body.Instructions.Count == 0) return;
            var ins = body.Instructions;

            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldstr) continue;
                string s = ins[i].Operand as string;
                if (!IsMarker(s, rules)) continue;

                var site = new MarkerSite
                {
                    Token = method.MetadataToken.ToInt32(),
                    DeclaringType = method.DeclaringType.FullName,
                    MethodName = method.Name,
                    Signature = DescribeSignature(method),
                    IlSize = (int)body.CodeSize
                };

                int end = Math.Min(ins.Count, i + 7);
                var sb = new StringBuilder();
                for (int j = i; j < end; j++)
                {
                    if (j > i) sb.Append(" ; ");
                    sb.Append(Describe(ins[j]));
                }
                site.Window = sb.ToString();
                site.Kind = Classify(ins, i, rules, body.Instructions.Count);
                site.Action = DefaultAction(site.Kind);
                report.Sites.Add(site);
            }
        }

        private static SiteKind Classify(IList<Instruction> ins, int i, Rules rules, int total)
        {
            bool compare = false, branch = false, hint = false, throwNew = false;
            int limit = Math.Min(ins.Count, i + 7);
            for (int j = i + 1; j < limit; j++)
            {
                var op = ins[j].OpCode;
                var mr = ins[j].Operand as MethodReference;
                if (mr != null)
                {
                    if (rules.CompareMethodNames.Any(n => mr.Name == n)) compare = true;
                    if (rules.HintMethodNames.Any(n => mr.Name == n)) hint = true;
                    if (op == OpCodes.Newobj && mr.DeclaringType.FullName == rules.ExceptionType &&
                        mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String")
                        throwNew = true;
                }
                if (op.FlowControl == FlowControl.Cond_Branch) branch = true;
            }

            if (compare && branch) return SiteKind.CompareTrap;
            if (throwNew)
                return total <= 4 ? SiteKind.StubThrow : SiteKind.InlineThrow;
            if (hint) return SiteKind.Hint;
            return SiteKind.Unknown;
        }

        private static string DefaultAction(SiteKind kind)
        {
            switch (kind)
            {
                case SiteKind.StubThrow: return "prefix-skip（作废桩方法）";
                case SiteKind.InlineThrow: return "整体替换方法体（整合包）/ 无 catch 上下文则跳过";
                case SiteKind.Hint: return "prefix-skip（取消下架提示）";
                case SiteKind.CompareTrap: return "none（真实运行时比较，严禁修改）";
                default: return "none（未识别，仅告警）";
            }
        }

        private static void ScanLoader(ModuleDefinition module, Rules rules, ScanReport report)
        {
            if (rules.Loader.Mode == "none") return;
            var type = FindType(module, rules.Loader.PageType);
            if (type == null) return;
            var method = type.Methods.FirstOrDefault(m => m.Name == rules.Loader.MethodName && m.HasBody);
            if (method == null) return;

            var site = new LoaderSite
            {
                Present = true,
                Token = method.MetadataToken.ToInt32(),
                DeclaringType = type.FullName,
                MethodName = method.Name,
                LoaderField = rules.Loader.LoaderFieldType + "::" + rules.Loader.LoaderFieldName
            };

            var ins = method.Body.Instructions;
            site.Newobjs = new List<string>();
            site.Calls = new List<string>();
            foreach (var x in ins)
            {
                var mrx = x.Operand as MethodReference;
                if (mrx == null) continue;
                if (x.OpCode == OpCodes.Newobj)
                    site.Newobjs.Add(mrx.DeclaringType.FullName + "::.ctor(" + string.Join(", ", mrx.Parameters.Select(q => q.ParameterType.FullName)) + ")");
                else if (x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt)
                {
                    if (mrx.Name.IndexOf("PageLoaderInit", StringComparison.Ordinal) >= 0 || mrx.Name.IndexOf("Loader", StringComparison.Ordinal) >= 0)
                        site.Calls.Add(mrx.DeclaringType.FullName + "::" + mrx.Name + "(" + string.Join(", ", mrx.Parameters.Select(q => q.ParameterType.FullName)) + ")");
                }
            }
            var hint = ins.FirstOrDefault(x => x.OpCode == OpCodes.Ldstr && (string)x.Operand == rules.Loader.HintTitle);
            if (hint != null) site.DummyTitle = rules.Loader.HintTitle;

            bool isLdcI4 = false;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Newobj) continue;
                var mr = ins[i].Operand as MethodReference;
                if (mr == null || mr.DeclaringType == null) continue;
                if (mr.DeclaringType.Name.IndexOf("LoaderTask", StringComparison.Ordinal) != 0 &&
                    (mr.DeclaringType.Namespace == null || mr.DeclaringType.Namespace.IndexOf("LoaderTask", StringComparison.Ordinal) < 0)) continue;
                site.DummyCtor = mr.DeclaringType.FullName + "::.ctor(" + string.Join(",", mr.Parameters.Select(q => q.ParameterType.Name)) + ")";
                OpCode? prev = i >= 1 ? ins[i - 1].OpCode : (OpCode?)null;
                isLdcI4 = prev.HasValue && (prev.Value == OpCodes.Ldc_I4 || prev.Value == OpCodes.Ldc_I4_S ||
                          ((int)prev.Value.Code >= 0x16 && (int)prev.Value.Code <= 0x1e));
                if (mr.Parameters.Count >= 1) site.PatternMatched = true;
            }
            site.Present = site.DummyCtor != null && site.DummyTitle != null;
            report.Loader = site;
        }

        private static void ScanUpdates(ModuleDefinition module, Rules rules, ScanReport report)
        {
            if (!rules.UpdateBlock.Enabled) return;
            var type = FindType(module, rules.UpdateBlock.Type);
            if (type == null)
            {
                report.Warnings.Add("未找到更新模块类型 " + rules.UpdateBlock.Type);
                return;
            }
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in type.Methods)
            {
                string hit = rules.UpdateBlock.MethodPrefixes.FirstOrDefault(p => MatchesUpdateMethod(m.Name, p));
                if (hit == null) continue;
                matched.Add(hit);
                report.UpdateTargets.Add(new UpdateTarget
                {
                    Token = m.MetadataToken.ToInt32(),
                    DeclaringType = type.FullName,
                    MethodName = m.Name,
                    Signature = DescribeSignature(m)
                });
            }
            if (report.UpdateTargets.Count == 0)
                report.Warnings.Add("更新禁用：在 " + rules.UpdateBlock.Type + " 中未匹配到任何方法。");
            // 规则里的前缀一个都没命中，多半是 PCL 改名了；别让它静默失效
            foreach (var p in rules.UpdateBlock.MethodPrefixes.Where(p => !matched.Contains(p)))
                report.Warnings.Add("更新禁用：规则前缀 “" + p + "” 未匹配到任何方法（可能已改名或移除）。");
        }

        /// <summary>
        /// 规则里写属性名也要能命中它的访问器：PCL 的更新权限是属性 HasUpdatePermission，
        /// 编译出来只有 get_HasUpdatePermission / set_HasUpdatePermission，按裸名前缀匹配会永远失败。
        /// </summary>
        private static bool MatchesUpdateMethod(string methodName, string prefix)
        {
            if (methodName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return methodName.StartsWith("get_" + prefix, StringComparison.Ordinal)
                || methodName.StartsWith("set_" + prefix, StringComparison.Ordinal);
        }

        public static string DescribeSignature(MethodDefinition m)
        {
            var sb = new StringBuilder();
            sb.Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(m.Parameters[i].ParameterType.Name).Append(" ").Append(m.Parameters[i].Name);
            }
            sb.Append(")");
            return sb.ToString();
        }

        public static string Describe(Instruction ins)
        {
            var op = ins.OpCode.Name;
            var operand = ins.Operand;
            if (operand == null) return op;
            if (operand is string) return op + " \"" + Short((string)operand, 48) + "\"";
            var mr = operand as MethodReference;
            if (mr != null) return op + " " + mr.DeclaringType.Name + "::" + mr.Name;
            var fr = operand as FieldReference;
            if (fr != null) return op + " " + fr.DeclaringType.Name + "::" + fr.Name;
            return op + " " + operand;
        }

        private static string Short(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        public static bool IsMarker(string s, Rules rules)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var p in rules.MarkerPrefixes)
                if (!string.IsNullOrEmpty(p) && s.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        public static TypeDefinition FindType(ModuleDefinition module, string fullName)
        {
            foreach (var t in AllTypes(module))
                if (t.FullName == fullName) return t;
            return null;
        }

        public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var t in module.Types)
                foreach (var x in Walk(t)) yield return x;
        }

        private static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
                foreach (var x in Walk(n)) yield return x;
        }

        public static IEnumerable<MethodDefinition> AllMethods(ModuleDefinition module)
        {
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods) yield return m;
        }

        private static int CountTypes(ModuleDefinition m) { return AllTypes(m).Count(); }
        private static int CountMethods(TypeDefinition t) { return t.Methods.Count + t.NestedTypes.Sum(CountMethods); }
        private static int CountFields(TypeDefinition t) { return t.Fields.Count + t.NestedTypes.Sum(CountFields); }
    }
}
