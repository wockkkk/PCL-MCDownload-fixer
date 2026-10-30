// PCL MCDownload fixer 备用构建脚本（Node.js）：与 build.ps1 等价，产出单文件
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const src = path.join(root, "src", "PCLauncher");
const lib = path.join(root, "lib");
const out = path.join(root, "PCL-MCDownload-fixer");

function findDotnet() {
  const p = path.join(process.env.ProgramFiles || "C:\\Program Files", "dotnet", "dotnet.exe");
  if (fs.existsSync(p)) return p;
  throw new Error("未找到 dotnet.exe（需要 .NET SDK 的 Roslyn 编译器）");
}
function findCsc() {
  const sdkRoot = path.join(process.env.ProgramFiles || "C:\\Program Files", "dotnet", "sdk");
  for (const d of fs.readdirSync(sdkRoot).sort().reverse()) {
    const c = path.join(sdkRoot, d, "Roslyn", "bincore", "csc.dll");
    if (fs.existsSync(c)) return c;
  }
  throw new Error("未找到带 Roslyn 的 .NET SDK");
}
function findRefs() {
  const pf86 = process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)";
  const p = path.join(pf86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
  if (fs.existsSync(p)) return p;
  const q = path.join(process.env.USERPROFILE || "", ".nuget", "packages",
    "microsoft.netframework.referenceassemblies.net48", "1.0.3", "build", ".NETFramework", "v4.8");
  if (fs.existsSync(q)) return q;
  throw new Error("未找到 .NET Framework 4.8 引用程序集");
}

const sources = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const q = path.join(d, e.name);
    if (e.isDirectory()) walk(q);
    else if (e.name.endsWith(".cs")) sources.push(q);
  }
})(src);

const refDir = findRefs();
const refs = ["mscorlib","System","System.Core","System.Web.Extensions","System.Windows.Forms","System.Drawing","System.Xml","System.IO.Compression","PresentationFramework","PresentationCore","WindowsBase"]
  .map(n => "/r:" + path.join(refDir, n + ".dll"));
refs.push("/r:" + path.join(lib, "Mono.Cecil.dll"), "/r:" + path.join(lib, "0Harmony.dll"));

fs.mkdirSync(out, { recursive: true });
const args = ["exec", findCsc(), "/nologo", "/target:winexe", "/platform:x86", "/langversion:latest",
  "/optimize+", "/nostdlib+", "/debug-", "/deterministic+",
  "/win32manifest:" + path.join(root, "build", "app.manifest"),
  "/out:" + path.join(out, "PCL-MCDownload-fixer.exe"),
  "/resource:" + path.join(src, "assets", "rules.json") + ",PCLauncher.rules.json",
  "/resource:" + path.join(lib, "Mono.Cecil.dll") + ",Mono.Cecil.dll",
  "/resource:" + path.join(lib, "0Harmony.dll") + ",0Harmony.dll",
  ...refs, ...sources];

const r = spawnSync(findDotnet(), args, { encoding: "utf8", stdio: "inherit" });
if (r.status !== 0) process.exit(r.status || 1);
console.log("PCL MCDownload fixer 构建完成（单文件）：" + path.join(out, "PCL-MCDownload-fixer.exe"));
