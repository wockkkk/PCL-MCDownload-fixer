# PCL MCDownload fixer —— PCL 下载 / 整合包功能恢复宿主

> **编写：DeepSeek-V4.1-Flash**（AI 编码代理）
>
> 本项目以 GPL-3.0 发布；宿主 + 运行时注入的行为模式可能被杀软误报，详见「10. 合规与风险」。

## 0. 快速上手（三步）

1. **先整体备份 PCL 目录**（本工具不写官方 exe，但备份永远是第一步）；
2. 把 **`PCL-MCDownload-fixer.exe`（单文件，2.5 MB）** 放进 PCL 目录（与官方 `Plain Craft Launcher 2.exe` 同级）；
3. 以后**双击 `PCL-MCDownload-fixer.exe`** 启动（会弹 UAC，点“是”——官方 PCL 本身也要求管理员），不要直接双击官方 exe，否则补丁不生效。

装好之后：

- **运行时文件都在 Fixer 自己目录下的 `runtime\`**（不写 PCL 目录）：
  - `runtime\logs\PCL-MCDownload-fixer-<年月日-时分秒>.log`
  - `runtime\reports\PCL-scan-<版本>.txt / .json`
  - `runtime\temp\`（系统临时目录不可写时才启用）
- 想改行为：编辑同目录 `rules.json`（删掉则用内嵌默认值）
- 自检：`PCL-MCDownload-fixer.exe --selftest`；全部参数：`--help`
- 官方 exe 启动前后会比对 SHA256 并写进日志，可自证零改动

> 宿主 + 运行时注入的行为模式与恶意软件相似，**可能被杀软误报**；使用与分发前请自行评估风险。
> 本项目不修改官方 `Plain Craft Launcher 2.exe` 的任何字节：所有改动都发生在进程内存里。

## 1. 它是什么

**PCL MCDownload fixer** 是一个外部启动器：双击它（而不是官方 exe）即可启动 PCL。
它在 PCL 的任何代码被 JIT 之前，完成三件事：

1. **只读扫描**磁盘上的官方 exe（Mono.Cecil），生成“拦截点清单”；
2. **运行时注入**（Lib.Harmony）——按清单精确放行被编译期常量折叠掉的功能；
3. **禁用官方更新**，防止一次自动更新把补丁绕过。

因为官方 exe 完全没被改写，官方升级后只要拦截形态没变，本工具无需改版。

## 2. 目录结构

```
PCL-MCDownload-fixer/
├─ README.md                     本说明
├─ LICENSE                       GPL-3.0
├─ .github/workflows/            自动构建 + tag 发布 Release
├─ src/PCLauncher/               C# 源码（Core / Scan / Patch / Update / Host / assets）
├─ build/                        构建脚本、app.manifest、selftest.ps1
└─ lib/                          编译期引用：Mono.Cecil / 0Harmony（构建时嵌入 exe）
```

构建产物 `PCL-MCDownload-fixer/`（单文件 exe）不纳入版本库，由 CI 产出：
push 到 main 可在 Actions 里下载 artifact，打 `v*` tag 会自动发 Release。

## 3. 构建

需要 Windows + .NET Framework 4.8 + .NET SDK（仅用于 Roslyn 编译器；不需要 NuGet 还原）。

```bat
build\build.cmd
```

产物是**单文件**：`PCL-MCDownload-fixer\PCL-MCDownload-fixer.exe`（约 2.5 MB）。

> 构建脚本用 `csc /resource:` 把 `0Harmony.dll`、`Mono.Cecil.dll`、`rules.json` 嵌进 exe，所以产物只有这一个文件。

备用方式：`build\build-node.cmd`（需要 Node.js，逻辑与 build.ps1 相同）；或用 Visual Studio 打开 `src/PCLauncher/PCLauncher.csproj`（net48 + x86，需先还原 Mono.Cecil / Lib.Harmony）。

> 若 PowerShell 提示 “AuthorizationManager 检查失败”，说明脚本所在磁盘被当作远程位置：把项目复制到本地磁盘，或改用 build-node.cmd。

## 4. 使用

把 `PCL-MCDownload-fixer.exe` 放到 PCL 目录（和官方 exe 同级），以后双击它启动。

```bat
PCL-MCDownload-fixer.exe                       启动 PCL（默认补丁）
PCL-MCDownload-fixer.exe --scan                只扫描不启动，输出拦截点报告
PCL-MCDownload-fixer.exe --pcl "D:\PCL\Plain Craft Launcher 2.exe"
PCL-MCDownload-fixer.exe --no-patch            不补丁，仅验证宿主
PCL-MCDownload-fixer.exe --no-update-block     不禁用更新
PCL-MCDownload-fixer.exe --no-loader           不改下载页加载器
PCL-MCDownload-fixer.exe --no-modpack          不恢复整合包手动安装入口
PCL-MCDownload-fixer.exe --temp "D:\PCL\temp"  指定 PCL 使用的临时目录
PCL-MCDownload-fixer.exe --selftest            运行内置自检（整合包类型识别）
PCL-MCDownload-fixer.exe --diag                诊断模式：记录 PCL 权限检查的真实异常
```

日志写入 `runtime\logs\PCL-MCDownload-fixer-时间戳.log`，扫描报告写入 `runtime\reports\PCL-scan-*.txt / .json`（`--out` 可改）；两者都在 Fixer 自己目录下，不写 PCL 目录。

## 5. 补丁策略（与源码形态一一对应）

| 形态 | 位置（2.99.0.1） | 处置 |
| --- | --- | --- |
| `stub-throw`（11 字节 lambda） | `PCL.PageDownloadInstall/_Closure$__::_Lambda$__3-0` | Harmony Prefix 返回 false，整个方法作废 |
| `inline-throw`（被删掉的分发点） | `PCL.ModModpack::ModpackInstall(String,String,String)` | **整体替换方法体**：原方法在 throw 之后的 `Select Case` 分发被编译器当作死代码删除，只改 throw 无法恢复安装；这里接管整个方法，重建类型识别并调用仍然完整的 `InstallPack*` |
| `hint`（下架提示） | `PCL.ModModpack::ModpackInstall()` | Prefix 替换为“弹文件选择框 → 调用三参重载”（**恢复手动安装整合包**入口） |
| `runtime-compare`（陷阱） | `PCL.FormMain/_Closure$__25-0::_Lambda$__0` | **绝不修改**：这是真实运行时字符串比较，只报告不 patch |
| 下载页加载器 | `PCL.PageDownloadInstall::LoaderInit` | Transpiler：把“构造伪 `LoaderTask("关闭下载", …, null, 2)`”弹出 4 个参数后改写为 `ldsfld PCL.ModDownload::DlClientListLoader` |
| 隐藏的“原版游戏”入口 | `PCL.PageDownloadLeft..ctor` | 发布版把 `ItemInstall` 与“社区资源”标题折叠了；Postfix 里通过反射把 `_ItemInstall` / `_LabResource` 设为 `Visible` |
| 更新禁用 | `PCL.ModSecret::UpdateCheckByButton / UpdateStart / UpdateRestart / UpdateReplace` | Prefix 跳过 |

所有匹配都基于 **指令序列（opcode + operand）**，不依赖偏移；匹配不到只告警、不崩溃，已识别的部分照常生效（Baseline 降级）。

打完补丁后还会对每个目标方法执行一次 `RuntimeHelpers.PrepareMethod` 强制 JIT，**验证改写出的 IL 合法**（日志里的 `[verify] JIT 校验通过 n/n`）。

> 为什么不能只删 throw：官方源码 `ModModpack.vb` 里 `If RestrictMcDownload Then Throw ...` 位于 `Select Case PackType` 之前。
> 常量折叠后，分发和所有 `InstallPack*` 调用都被判为不可达而删除，方法体在 throw 后直接进入 Finally。
> 所以本工具用 PCL 自身仍然导出的 `InstallPackCurseForge / InstallPackModrinth / InstallPackHMCL / InstallPackMMC / InstallPackMCBBS / InstallPackLauncherPack / InstallPackCompress` 重建分发。
> 下载页同理：源码 `PageDownloadInstall.LoaderInit` 的正常分支就是传 `DlClientListLoader`，本工具把伪 `LoaderTask` 的 4 个构造参数弹出后压入该字段，语义与官方关闭限制后完全一致。

## 6. 关键工程细节

- **宿主**：新建 AppDomain（`ApplicationBase` = PCL 目录、`ConfigurationFile` 优先复用 PCL 自带的 exe.config，没有则为空——单文件版不再生成临时 config），`Assembly.LoadFrom` 后先补丁再反射调用 `EntryPoint`，主线程 `[STAThread]`。
- **内嵌依赖预载**：PCL 把 MeloongCore / PCLCS / Newtonsoft.Json 等以资源形式内嵌在 exe 里，并且是在自己的启动流程中才注册 AssemblyResolve。为了让 Harmony 在补丁阶段就能解析方法签名，宿主会先把这些内嵌程序集预载进域。
- **零侵入**：只 hook 目标方法；所有 patch 归到一个 Harmony id `PCL.PCLauncher.Recovery`，便于整包卸载排查。
- **只读分析**：Cecil 以 `InMemory` 方式打开文件，不持有文件句柄、不写回。
- **哈希自证**：启动前后各算一次 SHA256 并写入日志。


## 6.5 宿主兼容层（实机踩坑记录）

把 PCL 塞进自己的 AppDomain 后，会依次撞到下面这些“宿主特有”的问题。它们和下载/整合包补丁无关，但不解决就根本进不去主界面。每一项都做成了可开关的规则：

| 现象 | 根因 | 处理 | 规则键 |
| --- | --- | --- | --- |
| “PCL 没有对当前文件夹的权限” | PCL 用 `\\?\` 长路径 API，而宿主进程默认是旧式路径处理，`Directory.CreateDirectory` 直接抛 `ArgumentException` | `MeloongCore.PathUtils.ForApi` 的 Postfix 去掉 `\\?\` 前缀（早期版本靠 exe.config 开关，单文件版改为代码级） | 配置文件 |
| “值不能为 null，参数名 resourceAssembly”（SplashScreen） | 宿主进程的 EntryAssembly 是 PCL-MCDownload-fixer，WPF 找不到 PCL 的资源 | 在入口点前设置 `Application.ResourceAssembly = PCL 程序集`；仍失败则跳过启动画面 | `disableSplash` |
| 主窗口一显示就弹“程序出现未知错误：拒绝访问”，堆栈在 `DragHelper.ChangeMessageFilter` | `ChangeWindowMessageFilterEx` 在受限令牌/低完整性进程里返回 5（拒绝访问），PCL 没捕获 | 只吞掉 `Win32Exception` 且 `NativeErrorCode == 5`，其它异常照常抛 | `environmentCompat` |
| 权限检查失败 | PCL 用“写临时文件再删除”探测权限，受限环境里删除会被拦 | 换成“只创建不删除”的等效探测 | `permissionCompat` |
| 红条“写入注册表失败 / 程序权限不足”，拖拽钩子 `拒绝访问` | 官方 PCL 的清单是 `requireAdministrator`（每次启动弹 UAC），而我们的启动器是 `asInvoker`，普通权限下写不了 `HKCU\Software\PCL`、也设不了窗口消息过滤器 | `build/app.manifest` 同样声明 `requireAdministrator`，与官方一致 | 编译清单 |
| 下载页左侧看不到“原版游戏” | 发布版 XAML 里 `ItemInstall` 和“社区资源”标题被设为 `Collapsed`（UIA 实测：offscreen=True、矩形 Infinity，且 ScrollViewer 不可滚动，排除“滚出视口”） | `PageDownloadLeft` 构造后强制设为 `Visible` | `restoreInstallEntry` |
| 启动即崩：`FileNotFoundException: Mono.Cecil` | 发行包里的 Mono.Cecil 版本与编译时不一致（0.11.5 在部分机器上解析失败） | 升级到 Mono.Cecil 0.11.6，并在启动时做依赖自检 + 目录兜底加载 | 依赖清单 |

> 注意：以上兼容层默认全部开启，但都可以用 `rules.json` 单独关掉；它们不改变 PCL 的业务逻辑，只修宿主环境差异。

## 7. 自检

不启动 PCL 也能验证核心逻辑：

```bat
PCL-MCDownload-fixer.exe --selftest
```

会用临时压缩包跑 8 个整合包类型识别用例（Modrinth / CurseForge / MCBBS / HMCL / MMC / 启动器包 / 未知 / addons 变体），全部通过返回 0。

## 8. 验证结果（本机实测）

- 扫描层：对 PCL 2.99.0.0 / 2.99.0.1 / 2.13.1.1 做过解析验证（运行时用 `--scan` 随时复查）。
- 2.99.0.0 与 2.99.0.1 都是 **4 个拦截点**，形态完全一致；但**文案不同**（2.99.0.0 是“PCL 暂时不再提供…”，2.99.0.1 是“PCL 不再提供…”），所以规则按前缀“为遵守相关法律法规”匹配，避免跨版本失效。
- 2.13.1.1（限制上线前）**0 个拦截点**，可作对照基线。
- 宿主：PCL 程序集加载成功；**15 项补丁全部成功**（0 失败、1 个陷阱点跳过）；8/8 个目标方法 JIT 校验通过；内嵌依赖（MeloongCore / PCLCS / Newtonsoft.Json 等 10 个）预载成功。
- 自检：整合包类型识别 8/8 通过。
- 官方 exe：在全部实验前后哈希一致（运行时会自动比对并写日志，可自证零改动）。

## 9. 已知限制

- 本仓库不包含官方 PCL 程序（版权原因），需要你自备 `Plain Craft Launcher 2.exe`；在自动化/沙箱环境里 PCL 自身可能因“无权写临时目录 / 注册表”弹“运行环境错误”，因此“完整下载 MC 客户端 / 安装整合包”这类 GUI 端到端验证请在正常桌面会话里完成。
  实测佐证：本次开发环境的目录被加了 `Everyone:(DENY)(S,DC)` + `Low Mandatory Level:(NW)` 的完整性标签（`icacls` 可见），PCL 的 `CheckPermissionWithException` 写入+删除探测文件时被拦；用直连、WMI、计划任务三种方式启动 PCLauncher 结果一致，说明与本工具无关。
- 建议的验收步骤：
  1. 备份 PCL 目录 → 双击 PCL-MCDownload-fixer.exe，确认主界面、主题、正版登录、启动游戏与官方一致；
  2. 进“下载”页，确认版本列表加载、能完整下载一个客户端；
  3. 拖入一个 Modrinth / CurseForge 整合包，或点“手动安装整合包”，确认能安装；
  4. 重启 PCL-MCDownload-fixer，确认补丁仍然生效；手动点“检查更新”应无反应；
  5. 对比官方 exe 的 SHA256（日志里就有）。
- 若日志里出现“未识别形态”“未匹配到伪 LoaderTask 构造”，说明该版本拦截形态变了：把 `runtime\reports\PCL-scan-*.txt` 反馈后按需更新 `rules.json` 即可，不需要改代码。

## 10. 合规与风险

- 本项目以 **GPL-3.0** 发布，**允许再分发**（保留版权与许可声明即可）。
- 宿主 + 运行时注入的行为模式与恶意软件相似，**可能被杀毒软件误报**；本项目不会尝试绕过任何安全软件。
- PCL 作者是出于合规原因主动下架这些功能的。绕过该限制可能触及当地法律法规或 PCL 的许可条款，**使用与分发的风险由你自行承担**；本 README 不构成法律意见。
- 本仓库不包含官方 `Plain Craft Launcher 2.exe`，也不修改它；其版权归 PCL 作者所有。

## 11. 许可

本项目以 **GNU General Public License v3.0** 发布，全文见 [LICENSE](LICENSE)。

- 依赖：Lib.Harmony（MIT）、Mono.Cecil（MIT），与本项目一起分发。
- 上游项目（PCL 本体）：<https://github.com/meloong-Git/PCL>
- 官方 `Plain Craft Launcher 2.exe` 不属于本项目，未修改、不随附源码，其版权归 PCL 作者所有。
- 再分发本项目的源码或编译产物时，请保留 `LICENSE` 与版权声明；官方 PCL 不在本项目授权范围内。

## 12. 持续集成 / 发布

`.github/workflows/build-release.yml`：

- **push / PR / 手动触发**：Windows runner 上跑 `build/build.ps1` 构建单文件 exe，校验大小、内嵌资源（0Harmony / Mono.Cecil / rules）、`requireAdministrator` 清单，并执行 `--selftest`；
- **打 tag（`v*`）**：自动创建 GitHub Release，附上 `PCL-MCDownload-fixer.exe`（Release 说明里带 SHA256）。

发布流程：`git tag v1.0.0 && git push origin v1.0.0`。

## 13. 声明

本项目的源码（`src/`）、构建脚本（`build/`）、GitHub Actions 配置以及本文档，
均由 **DeepSeek-V4.1-Flash** 编写与整理；需求、验收与最终取舍由使用者决定。

如发现代码或文档问题，欢迎提 issue。
