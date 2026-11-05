// 生成 Win32 资源文件（version.res）：VERSIONINFO + 应用清单 + 图标
// 注意：csc 的 /win32res 与 /win32icon、/win32manifest 互斥，所以图标与清单都并入 .res
// 版本号来源：build/version.txt；清单来源：build/app.manifest
// 用法：node build/make-res.mjs
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const versionFile = path.join(root, "build", "version.txt");
const manifestFile = path.join(root, "build", "app.manifest");
const outFile = path.join(root, "build", "version.res");

const version = fs.readFileSync(versionFile, "utf8").trim();
if (!/^\d+\.\d+\.\d+(\.\d+)?$/.test(version)) throw new Error("build/version.txt 版本号格式不对：" + version);
const parts = version.split(".").map((x) => parseInt(x, 10));
while (parts.length < 4) parts.push(0);
const [v0, v1, v2, v3] = parts;
const verNum = ((v0 & 0xffff) << 48) | ((v1 & 0xffff) << 32) | ((v2 & 0xffff) << 16) | (v3 & 0xffff);

const strings = {
  CompanyName: "wockkkk",
  FileDescription: "PCL 下载 / 整合包功能恢复宿主",
  FileVersion: version + (version.split(".").length < 4 ? ".0" : ""),
  InternalName: "PCL-MCDownload-fixer",
  LegalCopyright: "GPL-3.0；编写：DeepSeek-V4.1-Flash",
  OriginalFilename: "PCL-MCDownload-fixer.exe",
  ProductName: "PCL MCDownload fixer",
  ProductVersion: version,
  Comments: "https://github.com/wockkkk/PCL-MCDownload-fixer"
};

const align = (n) => (n + 3) & ~3;
const pad = (buf, size) => {
  const out = Buffer.alloc(align(size));
  buf.copy(out);
  return out;
};

// 一个 VERSIONINFO 节点：wLength, wValueLength, wType, szKey, padding, Value, padding, Children
function node(key, value, type, children, lengthInBytes) {
  const keyBuf = Buffer.from(key + "\u0000", "utf16le");
  const valueBuf = value || Buffer.alloc(0);
  const childBuf = children && children.length ? Buffer.concat(children) : Buffer.alloc(0);
  // 规范：key 从偏移 6 开始，其后填充到相对节点起点的 4 字节边界；value 同理
  const keyPad = (4 - ((6 + keyBuf.length) % 4)) % 4;
  const headerSize = 6 + keyBuf.length + keyPad;
  const valuePad = (4 - (valueBuf.length % 4)) % 4;
  const total = headerSize + valueBuf.length + valuePad + childBuf.length;
  const head = Buffer.alloc(6);
  head.writeUInt16LE(total, 0);
  head.writeUInt16LE(lengthInBytes ? valueBuf.length : Math.floor(valueBuf.length / 2), 2);
  head.writeUInt16LE(type, 4);
  return Buffer.concat([head, keyBuf, Buffer.alloc(keyPad), valueBuf, Buffer.alloc(valuePad), childBuf]);
}

const fixed = Buffer.alloc(52);
fixed.writeUInt32LE(0xFEEF04BD, 0);      // dwSignature
fixed.writeUInt32LE(0x00010000, 4);      // dwStrucVersion
fixed.writeUInt32LE(((v0 & 0xffff) << 16) | (v1 & 0xffff), 8);   // dwFileVersionMS
fixed.writeUInt32LE(((v2 & 0xffff) << 16) | (v3 & 0xffff), 12);  // dwFileVersionLS
fixed.writeUInt32LE(((v0 & 0xffff) << 16) | (v1 & 0xffff), 16);
fixed.writeUInt32LE(((v2 & 0xffff) << 16) | (v3 & 0xffff), 20);
fixed.writeUInt32LE(0x3F, 24);           // dwFileFlagsMask
fixed.writeUInt32LE(0, 28);              // dwFileFlags
fixed.writeUInt32LE(0x40004, 32);        // dwFileOS = VOS_NT_WINDOWS32
fixed.writeUInt32LE(0x1, 36);            // dwFileType = VFT_APP
fixed.writeUInt32LE(0, 40);              // dwFileSubtype
fixed.writeUInt32LE(0, 44);
fixed.writeUInt32LE(0, 48);

const stringEntries = Object.entries(strings).map(([k, v]) =>
  node(k, Buffer.from(v + "\u0000", "utf16le"), 1, null));
const stringTable = node("0" + "8" + "04" + "04" + "b0", null, 1, stringEntries); // 语言 0x0804 / 代码页 1200
const stringFileInfo = node("StringFileInfo", null, 1, [stringTable]);
const translation = Buffer.alloc(4);
translation.writeUInt16LE(0x0804, 0);
translation.writeUInt16LE(1200, 2);
const varFileInfo = node("VarFileInfo", null, 1, [node("Translation", translation, 0, null, true)]);
// VS_VERSION_INFO 的 wValueLength 是字节数（52 = VS_FIXEDFILEINFO 大小）
const versionInfo = node("VS_VERSION_INFO", fixed, 0, [stringFileInfo, varFileInfo], true);

const manifest = fs.readFileSync(manifestFile);

// ---- 图标：从 app.ico 拆成 RT_ICON 组 + RT_GROUP_ICON ----
const ico = fs.readFileSync(path.join(root, "build", "app.ico"));
const iconCount = ico.readUInt16LE(4);
const iconImages = [];
const groupEntries = [];
for (let i = 0; i < iconCount; i++) {
  const e = 6 + i * 16;
  const width = ico[e] || 256;
  const height = ico[e + 1] || 256;
  const colorCount = ico[e + 2];
  const planes = ico.readUInt16LE(e + 4);
  const bitCount = ico.readUInt16LE(e + 6);
  const bytes = ico.readUInt32LE(e + 8);
  const offset = ico.readUInt32LE(e + 12);
  iconImages.push({ id: i + 1, data: ico.subarray(offset, offset + bytes) });
  const ge = Buffer.alloc(14);
  ge[0] = width >= 256 ? 0 : width;
  ge[1] = height >= 256 ? 0 : height;
  ge[2] = colorCount;
  ge[3] = 0;
  ge.writeUInt16LE(planes || 1, 4);
  ge.writeUInt16LE(bitCount || 32, 6);
  ge.writeUInt32LE(bytes, 8);
  ge.writeUInt16LE(i + 1, 12);
  groupEntries.push(ge);
}
const groupHeader = Buffer.alloc(6);
groupHeader.writeUInt16LE(0, 0);
groupHeader.writeUInt16LE(1, 2);
groupHeader.writeUInt16LE(iconCount, 4);
const groupData = Buffer.concat([groupHeader, ...groupEntries]);

function resEntry(type, name, data, lang) {
  const headerSize = 0x20;
  const entry = Buffer.alloc(headerSize);
  entry.writeUInt32LE(data.length, 0);       // DataSize
  entry.writeUInt32LE(headerSize, 4);        // HeaderSize
  entry.writeUInt16LE(0xffff, 8);            // Type: ordinal
  entry.writeUInt16LE(type, 10);
  entry.writeUInt16LE(0xffff, 12);           // Name: ordinal
  entry.writeUInt16LE(name, 14);
  entry.writeUInt32LE(0, 16);                // DataVersion
  entry.writeUInt16LE(0x0030, 20);           // MemoryFlags
  entry.writeUInt16LE(lang, 22);             // LanguageId
  entry.writeUInt32LE(0, 24);                // Version
  entry.writeUInt32LE(0, 28);                // Characteristics
  return Buffer.concat([entry, pad(data, data.length)]);
}

const res = Buffer.concat([
  resEntry(16, 1, versionInfo, 0x0804),                 // RT_VERSION
  resEntry(24, 1, manifest, 0x0409),                    // RT_MANIFEST
  resEntry(14, 1, groupData, 0x0409),                   // RT_GROUP_ICON
  ...iconImages.map((i) => resEntry(3, i.id, i.data, 0x0409))  // RT_ICON
]);
fs.writeFileSync(outFile, res);
console.log("已生成 " + outFile + "（" + res.length + " 字节，版本 " + version + "）");

// 托管程序集信息：和 Win32 版本资源保持同一版本号
const asmInfo = path.join(root, "src", "PCLauncher", "VersionInfo.g.cs");
const cs = [
  "// 由 build/make-res.mjs 自动生成，请勿手改；版本号来源：build/version.txt",
  "using System.Reflection;",
  "using System.Runtime.InteropServices;",
  "",
  '[assembly: AssemblyTitle("PCL MCDownload fixer")]',
  '[assembly: AssemblyDescription("PCL 下载 / 整合包功能恢复宿主")]',
  '[assembly: AssemblyProduct("PCL MCDownload fixer")]',
  '[assembly: AssemblyCompany("wockkkk")]',
  '[assembly: AssemblyCopyright("GPL-3.0；编写：DeepSeek-V4.1-Flash")]',
  '[assembly: ComVisible(false)]',
  '[assembly: AssemblyVersion("' + version + (version.split(".").length < 4 ? ".0" : "") + '")]',
  '[assembly: AssemblyFileVersion("' + version + (version.split(".").length < 4 ? ".0" : "") + '")]',
  '[assembly: AssemblyInformationalVersion("' + version + '")]',
  ""
].join("\r\n");
fs.writeFileSync(asmInfo, cs, "utf8");
console.log("已生成 " + asmInfo);

// 顺便转成 csc 需要的 COFF 资源对象（.res 本身 csc 不认）
import { spawnSync as run } from "node:child_process";
const winDir = process.env.WINDIR || "C:\\Windows";
const cvtres = [
  path.join(winDir, "Microsoft.NET", "Framework", "v4.0.30319", "cvtres.exe"),
  path.join(winDir, "Microsoft.NET", "Framework64", "v4.0.30319", "cvtres.exe")
].find((x) => fs.existsSync(x));
if (cvtres) {
  const obj = path.join(root, "build", "version.obj");
  const r = run(cvtres, ["/machine:x86", "/out:" + obj, outFile], { encoding: "utf8" });
  if (r.status !== 0) throw new Error("cvtres 转换资源失败：" + (r.stderr || r.stdout || ""));
  console.log("已生成 " + obj);
} else {
  console.warn("未找到 cvtres.exe，跳过 version.obj 生成（需使用仓库内已提交的版本）");
}
