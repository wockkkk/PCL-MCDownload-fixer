using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PCLauncher.Core
{
    /// <summary>不依赖 PCL 运行环境的自检：验证整合包类型识别规则。</summary>
    public static class SelfTest
    {
        public static int RunModpackDetection(string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            var cases = new[]
            {
                new object[] { "modrinth.index.json", 4, "" },
                new object[] { "manifest.json", 0, "" },
                new object[] { "mcbbs.packmeta", 3, "" },
                new object[] { "mmc-pack.json", 2, "" },
                new object[] { "modpack.json", 1, "" },
                new object[] { "modpack.mrpack", 9, "" },
                new object[] { "random.txt", -1, "" },
            };

            int pass = 0, fail = 0;
            foreach (var c in cases)
            {
                string file = Path.Combine(tempDir, "case-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var fs = new FileStream(file, FileMode.Create))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, (string)c[0], "{}");
                }
                string baseFolder;
                int got;
                using (var zip = ZipFile(file))
                    got = Patch.ModpackInstaller.DetectPackType(zip, out baseFolder);
                bool ok = got == (int)c[1];
                if (ok) pass++; else fail++;
                Console.WriteLine((ok ? "  PASS " : "  FAIL ") + c[0] + " -> " + got + "（期望 " + c[1] + "）");
                File.Delete(file);
            }

            // MCBBS 变体：manifest.json 中含 addons
            {
                string file = Path.Combine(tempDir, "case-addons.zip");
                using (var fs = new FileStream(file, FileMode.Create))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    WriteEntry(zip, "manifest.json", "{\"addons\":[]}");
                string baseFolder;
                int got;
                using (var zip = ZipFile(file)) got = Patch.ModpackInstaller.DetectPackType(zip, out baseFolder);
                bool ok = got == 3 && baseFolder == "overrides/";
                if (ok) pass++; else fail++;
                Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "manifest.json(addons) -> " + got + " base=" + baseFolder);
                File.Delete(file);
            }

            Console.WriteLine("整合包识别自检：" + pass + " 通过，" + fail + " 失败。");
            return fail == 0 ? 0 : 1;
        }

        private static ZipArchive ZipFile(string path)
        {
            return new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name);
            using (var s = e.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
