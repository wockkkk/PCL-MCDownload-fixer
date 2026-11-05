// PCL MCDownload fixer 备用构建脚本（Node.js）：与 build.ps1 等价，产出单文件
// 与 build.ps1 保持同步：csc 响应文件、.NET SDK 版本数字排序、42/48 引用程序集 NuGet 兜底
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
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

function versionKey(name) {
  const parts = name.split(".").map((x) => parseInt(x, 10));
  return parts.map((x) => String(isNaN(x) ? 0 : x).padStart(6, "0")).join(".");
}

function findCsc() {
  const sdkRoot = path.join(process.env.ProgramFiles || "C:\\Program Files", "dotnet", "sdk");
  const dirs = fs.readdirSync(sdkRoot).sort((a, b) => versionKey(b).localeCompare(versionKey(a)));
  for (const d of dirs) {
    const c = path.join(sdkRoot, d, "Roslyn", "bin core".replace(" ", ""), "csc.dll");
    if (fs.existsSync(c)) return c;
  }
  throw new Error("未找到带 Roslyn 的 .NET SDK");
}

const refCandidates = () => {
  const pf86 = process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)";
  return [
    path.join(pf86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8"),
    path.join(process.env.USERPROFILE || "", ".nuget", "packages",
      "microsoft.netframework.referenceassemblies.net48", "1.0.3", "build", ".NETFramework", "v4.8")
  ];
};

function findRefs() {
  for (const p of refCandidates()) if (fs.existsSync(p)) return p;
  // CI / 干净机器兜底：从 NuGet 拉参考程序集包再解压
  const tmp = os.tmpdir();
  const pkg = path.join(tmp, "microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg");
  const dest = path.join(tmp, "net48refs");
  console.log("未找到 4.8 引用程序集，尝试从 NuGet 下载…");
  const url = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg";
  const curl = spawnSync("curl.exe", ["-L", "--fail", "-o", pkg, url], { stdio: "inherit" });
  if (curl.status !== 0) throw new Error("下载 4.8 引用程序集失败（curl exit " + curl.status + "）");
  fs.rmSync(dest, { recursive: true, force: true });
  let unzip = spawnSync("tar.exe", ["-xf", pkg, "-C", dest], { stdio: "inherit" });
  if (unzip.status !== 0) {
    fs.mkdirSync(dest, { recursive: true });
    unzip = spawnSync("powershell.exe", ["-NoProfile", "-Command",
      "Expand-Archive -Force -Path '" + pkg + "' -DestinationPath '" + dest + "'"], { stdio: "inherit" });
  }
  const refDir = path.join(dest, "build", ".NETFramework", "v4.8");
  if (!fs.existsSync(refDir)) throw new Error("未找到 .NET Framework 4.8 引用程序集");
  return refDir;
}

const sources = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const q = path.join(d, e.name);
    if (e.isDirectory()) {
      if (e.name === "bin" || e.name === "obj") continue;
      walk(q);
    } else if (e.name.endsWith(".cs")) sources.push(q);
  }
})(src);

const refDir = findRefs();
const refs = ["mscorlib", "System", "System.Core", "System.Web.Extensions", "System.Windows.Forms",
  "System.Drawing", "System.Xml", "System.IO.Compression", "PresentationFramework", "PresentationCore", "WindowsBase"]
  .map((n) => '/r:"' + path.join(refDir, n + ".dll") + '"');
refs.push('/r:"' + path.join(lib, "Mono.Cecil.dll") + '"', '/r:"' + path.join(lib, "0Harmony.dll") + '"');

fs.mkdirSync(out, { recursive: true });
const outExe = path.join(out, "PCL-MCDownload-fixer.exe");
const lines = [
  "/nologo",
  "/target:winexe",
  "/platform:anycpu",
  "/langversion:latest",
  "/optimize+",
  "/nostdlib+",
  "/debug-",
  "/deterministic+",
  '/win32manifest:"' + path.join(root, "build", "app.manifest") + '"',
  ...(fs.existsSync(path.join(root, "build", "app.ico"))
    ? ['/win32icon:"' + path.join(root, "build", "app.ico") + '"']
    : []),
  '/out:"' + outExe + '"',
  '/resource:"' + path.join(src, "assets", "rules.json") + '",PCLauncher.rules.json',
  '/resource:"' + path.join(lib, "Mono.Cecil.dll") + '",Mono.Cecil.dll',
  '/resource:"' + path.join(lib, "0Harmony.dll") + '",0Harmony.dll',
  ...refs,
  ...sources.map((s) => '"' + s + '"')
];

const rsp = path.join(os.tmpdir(), "PCL-MCDownload-fixer-csc.rsp");
fs.writeFileSync(rsp, lines.join("\r\n"), "utf8");
console.log("csc 响应文件：" + rsp + "（" + lines.length + " 行）");

const r = spawnSync(findDotnet(), ["exec", findCsc(), "@" + rsp], { encoding: "utf8", stdio: "inherit" });
try { fs.unlinkSync(rsp); } catch { /* ignore */ }
if (r.status !== 0) process.exit(r.status || 1);
console.log("PCL MCDownload fixer 构建完成（单文件）：" + outExe);
