using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using PCLauncher.Core;

namespace PCLauncher.Patch
{
    /// <summary>
    /// 整合包安装恢复。
    ///
    /// 官方源码（ModModpack.vb）：
    ///   If RestrictMcDownload Then Throw New Exception(...)
    ///   Select Case PackType : ... InstallPackXxx(...)
    /// 开关是编译期常量，所以“投掷”之后的 Select Case 分发被 Roslyn 判定为不可达并整体删除——
    /// 只把 throw 改成 rethrow 并不能恢复安装。这里用仍然存在的 InstallPack* 方法重建分发逻辑。
    /// </summary>
    public static class ModpackInstaller
    {
        public static object Install(string file, string instanceName, string logo, Type modpackType)
        {
            if (modpackType == null) throw new InvalidOperationException("未找到 PCL.ModModpack 类型。");
            if (string.IsNullOrEmpty(file)) throw new ArgumentNullException("file");

            Log.Info("[modpack] 安装请求：" + file);

            ZipArchive archive;
            try
            {
                // FileShare.ReadWrite：PCL 的下载器随后还要读这个文件；不独占、不长期锁死
                archive = new ZipArchive(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                                         ZipArchiveMode.Read);
            }
            catch (Exception ex)
            {
                throw new Exception("打开整合包文件失败，文件可能损坏或为不支持的压缩包格式", ex);
            }

            string baseFolder;
            int packType = DetectPackType(archive, out baseFolder);
            Log.Info("[modpack] 识别到整合包类型：" + packType + "，基准目录：" + baseFolder);

            MethodInfo target;
            object[] args;
            switch (packType)
            {
                case 0: target = Find(modpackType, "InstallPackCurseForge", 5); args = new object[] { file, archive, baseFolder, instanceName, logo }; break;
                case 1: target = Find(modpackType, "InstallPackHMCL", 3); args = new object[] { file, archive, baseFolder }; break;
                case 2: target = Find(modpackType, "InstallPackMMC", 3); args = new object[] { file, archive, baseFolder }; break;
                case 3: target = Find(modpackType, "InstallPackMCBBS", 4); args = new object[] { file, archive, baseFolder, instanceName }; break;
                case 4: target = Find(modpackType, "InstallPackModrinth", 5); args = new object[] { file, archive, baseFolder, instanceName, logo }; break;
                case 9: target = Find(modpackType, "InstallPackLauncherPack", 3); args = new object[] { file, archive, baseFolder }; break;
                default: target = Find(modpackType, "InstallPackCompress", 2); args = new object[] { file, archive }; break;
            }

            if (target == null)
                throw new MissingMethodException("当前 PCL 版本缺少整合包安装方法（" + packType + "）。");

            Log.Info("[modpack] 调用 " + target.Name);
            return target.Invoke(null, args);
        }

        /// <summary>按 PCL 源码 ModModpack.vb 的规则识别整合包类型。0=CurseForge 1=HMCL 2=MMC 3=MCBBS 4=Modrinth 9=启动器包 -1=普通压缩包。</summary>
        public static int DetectPackType(ZipArchive archive, out string baseFolder)
        {
            baseFolder = "";
            int packType = -1;
            if (archive.GetEntry("mcbbs.packmeta") != null) return 3;
            if (archive.GetEntry("mmc-pack.json") != null) return 2;
            if (archive.GetEntry("modrinth.index.json") != null) return 4;
            if (archive.GetEntry("manifest.json") != null)
            {
                if (ManifestHasAddons(archive)) { baseFolder = "overrides/"; return 3; }
                return 0;
            }
            if (archive.GetEntry("modpack.json") != null) return 1;
            if (archive.GetEntry("modpack.zip") != null || archive.GetEntry("modpack.mrpack") != null) return 9;

            foreach (var entry in archive.Entries)
            {
                var parts = entry.FullName.Split('/');
                if (parts.Length > 0) baseFolder = parts[0] + "/";
                if (parts.Length != 2) continue;
                switch (parts[1])
                {
                    case "mcbbs.packmeta": return 3;
                    case "mmc-pack.json": return 2;
                    case "modrinth.index.json": return 4;
                    case "manifest.json":
                        if (ManifestHasAddons(archive, parts[0] + "/manifest.json")) { baseFolder = "overrides/"; return 3; }
                        return 0;
                    case "modpack.json": return 1;
                    case "modpack.zip":
                    case "modpack.mrpack": return 9;
                }
            }
            return packType;
        }

        private static bool ManifestHasAddons(ZipArchive archive, string entryName = "manifest.json")
        {
            try
            {
                var e = archive.GetEntry(entryName);
                if (e == null) return false;
                using (var s = e.Open())
                using (var r = new StreamReader(s))
                {
                    string text = r.ReadToEnd();
                    return text.IndexOf("\"addons\"", StringComparison.Ordinal) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        private static MethodInfo Find(Type type, string name, int paramCount)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                       .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == paramCount);
        }
    }
}
