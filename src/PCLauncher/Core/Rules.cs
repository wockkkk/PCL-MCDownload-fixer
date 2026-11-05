using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PCLauncher.Core
{
    public sealed class LoaderRule
    {
        public string PageType = "PCL.PageDownloadInstall";
        public string MethodName = "LoaderInit";
        public string HintTitle = "关闭下载";
        public string LoaderFieldType = "PCL.ModDownload";
        public string LoaderFieldName = "DlClientListLoader";
        public string Mode = "replace";   // replace | skip | none
    }

    public sealed class UpdateRule
    {
        public string Type = "PCL.ModSecret";
        public string[] MethodPrefixes = { "UpdateCheckByButton", "UpdateStart", "UpdateRestart", "UpdateReplace", "DownloadLatestPCL", "HasUpdatePermission" };
        public bool Enabled = true;
    }

    public sealed class ModpackRule
    {
        public string Type = "PCL.ModModpack";
        public string MethodName = "ModpackInstall";
        public bool Enabled = true;
    }

    public sealed class Rules
    {
        public string[] MarkerPrefixes = { "为遵守相关法律法规" };
        public string[] HintMethodNames = { "Hint" };
        public string[] CompareMethodNames = { "CompareString" };
        public string ExceptionType = "System.Exception";
        public LoaderRule Loader = new LoaderRule();
        public UpdateRule UpdateBlock = new UpdateRule();
        public ModpackRule ModpackDialog = new ModpackRule();
        public bool PermissionCompat = true;
        public bool DisableSplash = true;
        public bool EnvironmentCompat = true;
        public bool RestoreInstallEntry = true;
        public bool RegistryCompat = false;
        public bool RestoreModpackRowClick = true;
        public bool RestoreSaveButton = true;
        public bool LongPathCompat = true;   // 关闭后不改写 \\?\ 长路径前缀（老系统 / 超长路径场景可关）
        public string Source = "内置默认值";

        public static Rules Load(string path, Options opt)
        {
            var r = new Rules();
            string json = null;

            if (!string.IsNullOrEmpty(path))
            {
                if (!File.Exists(path)) throw new FileNotFoundException("规则文件不存在：" + path);
                json = File.ReadAllText(path);
                r.Source = path;
            }
            else
            {
                string local = Path.Combine(opt.LauncherDir, "rules.json");
                if (File.Exists(local)) { json = File.ReadAllText(local); r.Source = local; }
                else
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("PCLauncher.rules.json"))
                    {
                        if (s != null)
                        {
                            using (var sr = new StreamReader(s)) json = sr.ReadToEnd();
                            r.Source = "内嵌资源 PCLauncher.rules.json";
                        }
                    }
                }
            }

            if (json == null) return r;

            Dictionary<string, object> d;
            try
            {
                d = Json.ParseObject(json);
            }
            catch (Exception ex)
            {
                // 用户手改 rules.json 写坏了不能让程序崩溃：告警并回落到内置默认值
                Log.Error("[rules] 规则文件解析失败，改用内置默认值：" + ex.Message + "（" + r.Source + "）");
                return r;
            }
            if (d == null)
            {
                Log.Warn("[rules] 规则文件不是合法的 JSON 对象，改用内置默认值：" + r.Source);
                return r;
            }

            r.MarkerPrefixes = StrArray(d, "markerPrefixes", r.MarkerPrefixes);
            r.HintMethodNames = StrArray(d, "hintMethodNames", r.HintMethodNames);
            r.CompareMethodNames = StrArray(d, "compareMethodNames", r.CompareMethodNames);
            r.ExceptionType = Str(d, "exceptionType", r.ExceptionType);
            r.PermissionCompat = Bool(d, "permissionCompat", r.PermissionCompat);
            r.DisableSplash = Bool(d, "disableSplash", r.DisableSplash);
            r.EnvironmentCompat = Bool(d, "environmentCompat", r.EnvironmentCompat);
            r.RestoreInstallEntry = Bool(d, "restoreInstallEntry", r.RestoreInstallEntry);
            r.RegistryCompat = Bool(d, "registryCompat", r.RegistryCompat);
            r.RestoreModpackRowClick = Bool(d, "restoreModpackRowClick", r.RestoreModpackRowClick);
            r.RestoreSaveButton = Bool(d, "restoreSaveButton", r.RestoreSaveButton);
            r.LongPathCompat = Bool(d, "longPathCompat", r.LongPathCompat);

            var loader = Obj(d, "loader");
            if (loader != null)
            {
                r.Loader.PageType = Str(loader, "pageType", r.Loader.PageType);
                r.Loader.MethodName = Str(loader, "methodName", r.Loader.MethodName);
                r.Loader.HintTitle = Str(loader, "hintTitle", r.Loader.HintTitle);
                r.Loader.LoaderFieldType = Str(loader, "loaderFieldType", r.Loader.LoaderFieldType);
                r.Loader.LoaderFieldName = Str(loader, "loaderFieldName", r.Loader.LoaderFieldName);
                r.Loader.Mode = Str(loader, "mode", r.Loader.Mode);
            }

            var upd = Obj(d, "updateBlock");
            if (upd != null)
            {
                r.UpdateBlock.Type = Str(upd, "type", r.UpdateBlock.Type);
                r.UpdateBlock.MethodPrefixes = StrArray(upd, "methodPrefixes", r.UpdateBlock.MethodPrefixes);
                r.UpdateBlock.Enabled = Bool(upd, "enabled", r.UpdateBlock.Enabled);
            }

            var mp = Obj(d, "modpackDialog");
            if (mp != null)
            {
                r.ModpackDialog.Type = Str(mp, "type", r.ModpackDialog.Type);
                r.ModpackDialog.MethodName = Str(mp, "methodName", r.ModpackDialog.MethodName);
                r.ModpackDialog.Enabled = Bool(mp, "enabled", r.ModpackDialog.Enabled);
            }

            if (opt != null)
            {
                if (opt.NoLoaderRestore) r.Loader.Mode = "none";
                if (opt.NoUpdateBlock) r.UpdateBlock.Enabled = false;
                if (opt.NoModpackDialog) r.ModpackDialog.Enabled = false;
            }
            return r;
        }

        private static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v;
            return d.TryGetValue(k, out v) && v is string ? (string)v : def;
        }

        private static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool ? (bool)v : def;
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        private static string[] StrArray(Dictionary<string, object> d, string k, string[] def)
        {
            object v;
            if (!d.TryGetValue(k, out v)) return def;
            var arr = v as object[];
            if (arr == null) return def;
            var list = new List<string>();
            foreach (var o in arr) if (o is string) list.Add((string)o);
            return list.Count > 0 ? list.ToArray() : def;
        }
    }
}
