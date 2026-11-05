# 开发文档（DEVELOPMENT）

面向要读源码、改补丁或参与构建的人。普通用户只需要看 [README.md](README.md)。

## 目录结构

```
PCL-MCDownload-fixer/
├─ README.md                     面向用户的使用说明
├─ DEVELOPMENT.md               本文件（工程细节）
├─ LICENSE                       GPL-3.0
├─ .github/workflows/            自动构建 + tag 发布 Release
├─ src/PCLauncher/               C# 源码（Core / Scan / Patch / Update / Host / assets）
├─ build/                        构建脚本、app.manifest、selftest.ps1
└─ lib/                          编译期引用：Mono.Cecil / 0Harmony（构建时嵌入 exe）
```

构建产物 `PCL-MCDownload-fixer/`（单文件 exe）不纳入版本库，由 CI 产出：
push 到 main 可在 Actions 里下载 artifact，打 `v*` tag 会自动发 Release。

## 构建

需要 Windows + .NET Framework 4.8 + .NET SDK（仅用于 Roslyn 编译器；不需要 NuGet 还原）。

```bat
build\build.ps1        # 主入口（等价于 build\build.cmd）
build\build-node.cmd   # 备用：需要 Node.js，逻辑与 build.ps1 相同
```

产物是**单文件**：`PCL-MCDownload-fixer\PCL-MCDownload-fixer.exe`（约 2.6 MB）。
构建脚本用 `csc /resource:` 把 `0Harmony.dll`、`Mono.Cecil.dll`、`rules.json` 嵌进 exe，所以产物只有这一个文件。
两个脚本已对齐：都通过 csc 响应文件传参（避免路径空格被拆开）、按版本号数字排序选 SDK、引用程序集缺失时从 NuGet 兜底下载，并跳过 `bin/`、`obj/`。
也可以直接用 Visual Studio 打开 `src/PCLauncher/PCLauncher.csproj`（net48 + AnyCPU，已配置 `Win32Resource`；VS 构建不是单文件，依赖 dll 会留在输出目录）。

> 若 PowerShell 提示 “AuthorizationManager 检查失败”，说明脚本所在磁盘被当作远程位置：把项目复制到本地磁盘，或改用 build-node.cmd。

### 版本信息、图标与清单

版本号唯一来源是 `build/version.txt`。构建前 `build/make-res.mjs` 生成三样东西：

- `build/version.res`：VERSIONINFO（文件说明 / 产品名 / 版本 / 版权等）+ 应用清单（requireAdministrator）+ 图标；
- `src/PCLauncher/VersionInfo.g.cs`：托管程序集属性（AssemblyVersion / AssemblyFileVersion 等）；
- `build/version.obj`：用系统自带 `cvtres.exe` 转出的 COFF 资源对象。

> csc 的 `/win32res` 与 `/win32icon`、`/win32manifest` 互斥，而且只认 COFF 资源对象，所以清单、图标、版本信息必须合并进同一个 `.res` 再由 `cvtres` 转换。
> 产物「属性 → 详细信息」里的文件说明、产品名、版本、版权都来自这里。

发版流程：改 `build/version.txt` → 构建（自动重生成上述文件）→ 产物属性里的版本号随之更新。

### 应用图标

`build/app.ico` 由 PCL 官方 exe 内嵌的图标资源提取后**略作修改**（右下角加一个绿色对勾徽标）生成，仅用于让 Fixer 在外观上和 PCL 一致、同时能与官方区分。
提取/改版脚本是一次性本地操作，未入库；仓库只保留成品 `app.ico`。该图标是 PCL 图标的衍生作品，版权归 PCL 作者，随本项目仅作视觉识别用途。

## 补丁策略（与源码形态一一对应）

| 形态 | 位置（2.99.0.1） | 处置 |
| --- | --- | --- |
| `stub-throw`（11 字节 lambda） | `PCL.PageDownloadInstall/_Closure$__::_Lambda$__3-0` | Harmony Prefix 返回 false，整个方法作废 |
| `inline-throw`（被删掉的分发点） | `PCL.ModModpack::ModpackInstall(String,String,String)` | **整体替换方法体**：原方法在 throw 之后的 `Select Case` 分发被编译器当作死代码删除，只改 throw 无法恢复安装；这里接管整个方法，重建类型识别并调用仍然完整的 `InstallPack*` |
| `hint`（下架提示） | `PCL.ModModpack::ModpackInstall()` | Prefix 替换为“弹文件选择框 → 调用三参重载”（恢复手动安装整合包入口） |
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

## 关键工程细节

- **宿主**：新建 AppDomain（`ApplicationBase` = PCL 目录、`ConfigurationFile` 优先复用 PCL 自带的 exe.config，没有则为空——单文件版不再生成临时 config），`Assembly.LoadFrom` 后先补丁再反射调用 `EntryPoint`，主线程 `[STAThread]`。
- **内嵌依赖预载**：PCL 把 MeloongCore / PCLCS / Newtonsoft.Json 等以资源形式内嵌在 exe 里，并且是在自己的启动流程中才注册 AssemblyResolve。为了让 Harmony 在补丁阶段就能解析方法签名，宿主会先把这些内嵌程序集预载进域。
- **零侵入**：只 hook 目标方法；所有 patch 归到一个 Harmony id `PCL.PCLauncher.Recovery`，便于整包卸载排查。
- **只读分析**：Cecil 以 `InMemory` 方式打开文件，不持有文件句柄、不写回。
- **哈希自证**：PCL 退出时直接调 `Environment.Exit`，进程级 `AppDomain.ProcessExit` 实测不触发（钩 `Environment.Exit` 也无效，它本身是内部实现），所以“退出后当场算哈希”不可行。改为跨次启动校验：`HashState` 每次启动前把哈希写进 `runtime\state\last-run.sha256`，下次启动先比对上次记录，一致即证明两次运行之间 exe 未被改写；PCL 正常返回（没走 Exit）时另有一次当场校验。

## 宿主兼容层（实机踩坑记录）

把 PCL 塞进自己的 AppDomain 后，会依次撞到下面这些“宿主特有”的问题。它们和下载/整合包补丁无关，但不解决就根本进不去主界面。每一项都做成了可开关的规则：

| 现象 | 根因 | 处理 | 规则键 |
| --- | --- | --- | --- |
| “PCL 没有对当前文件夹的权限” | PCL 用 `\?\` 长路径 API，而宿主进程默认是旧式路径处理，`Directory.CreateDirectory` 直接抛 `ArgumentException` | `MeloongCore.PathUtils.ForApi` 的 Postfix 去掉 `\?\` 前缀（早期版本靠 exe.config 开关，单文件版改为代码级） | 配置文件 |
| “值不能为 null，参数名 resourceAssembly”（SplashScreen） | 宿主进程的 EntryAssembly 是 PCL-MCDownload-fixer，WPF 找不到 PCL 的资源 | 在入口点前设置 `Application.ResourceAssembly = PCL 程序集`；仍失败则跳过启动画面 | `disableSplash` |
| 主窗口一显示就弹“程序出现未知错误：拒绝访问”，堆栈在 `DragHelper.ChangeMessageFilter` | `ChangeWindowMessageFilterEx` 在受限令牌/低完整性进程里返回 5（拒绝访问），PCL 没捕获 | 只吞掉 `Win32Exception` 且 `NativeErrorCode == 5`，其它异常照常抛 | `environmentCompat` |
| 权限检查失败 | PCL 用“写临时文件再删除”探测权限，受限环境里删除会被拦 | 换成“只创建不删除”的等效探测 | `permissionCompat` |
| 红条“写入注册表失败 / 程序权限不足”，拖拽钩子 `拒绝访问` | 官方 PCL 的清单是 `requireAdministrator`（每次启动弹 UAC），而我们的启动器是 `asInvoker`，普通权限下写不了 `HKCU\Software\PCL`、也设不了窗口消息过滤器 | `build/app.manifest` 同样声明 `requireAdministrator`，与官方一致 | 编译清单 |
| 下载页左侧看不到“原版游戏” | 发布版 XAML 里 `ItemInstall` 和“社区资源”标题被设为 `Collapsed`（UIA 实测：offscreen=True、矩形 Infinity，且 ScrollViewer 不可滚动，排除“滚出视口”） | `PageDownloadLeft` 构造后强制设为 `Visible` | `restoreInstallEntry` |
| 启动即崩：`FileNotFoundException: Mono.Cecil` | 发行包里的 Mono.Cecil 版本与编译时不一致（0.11.5 在部分机器上解析失败） | 升级到 Mono.Cecil 0.11.6，并在启动时做依赖自检 + 目录兜底加载 | 依赖清单 |
| 版本图标全部加载失败：`加载版本图标失败 … DllNotFoundException: libwebp` | 两个原因叠加：① Fixer 以 `/platform:x86` 构建，把 AnyCPU 的 PCL 拖进 32 位进程，而 PCL 内嵌的 `libwebp64` 是 64 位原生 DLL；② PCL 只在**自己的** AssemblyResolve 回调里才释放该原生库（`ExtractLibwebp()` → 写 `<PCL 工作目录>\libwebp.dll` + `SetDllDirectory`），宿主若抢先把 `Imazen.WebP` 预载进域，这个回调就永远不会触发 | ① 构建改为 `/platform:anycpu`，让 PCL 和官方一样在 x64 系统上以 64 位运行（32 位系统自动回退）；② `DependencyResolver` 增加延迟加载名单，`Imazen.WebP` 交给 PCL 自己加载；③ 启动时把宿主位宽与 PCL 的 CLR 头（`ILONLY` / `32BITREQUIRED`）写进日志，位宽不符时给出警告 | 构建平台 + 延迟加载名单 |

> 以上兼容层默认全部开启，但都可以用 `rules.json` 单独关掉；它们不改变 PCL 的业务逻辑，只修宿主环境差异。
>
> 位宽是硬约束：官方 PCL 是 AnyCPU（PE32 + ILONLY + 无 32BITREQUIRED），在 x64 系统上按 64 位跑，它内嵌的原生库（`libwebp64`、`libjlw-x86_64` 等）也只有 64 位版本。所以 Fixer 必须是 AnyCPU；如果在 32 位系统上运行，PCL 的这些图片解码功能本身就会失效（与官方行为一致）。

## 实测验证结果

- 扫描层：对 PCL 2.99.0.0 / 2.99.0.1 / 2.13.1.1 做过解析验证（运行时用 `--scan` 随时复查）。
- 2.99.0.0 与 2.99.0.1 都是 **4 个拦截点**，形态完全一致；但**文案不同**（2.99.0.0 是“PCL 暂时不再提供…”，2.99.0.1 是“PCL 不再提供…”），所以规则按前缀“为遵守相关法律法规”匹配，避免跨版本失效。
- 2.13.1.1（限制上线前）**0 个拦截点**，可作对照基线。
- 宿主：PCL 程序集加载成功；**16 项补丁全部成功**（0 失败、1 个陷阱点跳过）；JIT 校验通过；内嵌依赖（MeloongCore / PCLCS / Newtonsoft.Json 等 10 个）预载成功。
- 更新禁用：2.99.0.1 实测识别 **6 个目标**（4 个 Update* + `DownloadLatestPCL` + `get_HasUpdatePermission`）。规则里写属性名即可，扫描层会同时匹配 `get_`/`set_` 访问器——否则按裸名前缀匹配会永远落空。
- 尚未实机验证的部分（避免过度承诺）：`restoreModpackRowClick`、`restoreSaveButton`、文件面板“导入整合包”注入这三项目前只确认 hook 已挂载，还没有对应的点击执行记录。
- 自检：整合包类型识别 8/8 通过。
- 官方 exe：在全部实验前后哈希一致（运行时会自动比对并写日志，可自证零改动）。
- PCL 2.13.1.1（限制上线前版本，0 个拦截点）实机跑通：64 位宿主、11 项补丁全部成功、版本图标与 WebP 图片正常（`libwebp` 由 PCL 自行释放，宿主不再预载 `Imazen.WebP`）。
- 沙箱限制佐证：本次开发环境的目录被加了 `Everyone:(DENY)(S,DC)` + `Low Mandatory Level:(NW)` 的完整性标签（`icacls` 可见），PCL 的 `CheckPermissionWithException` 写入+删除探测文件时被拦；用直连、WMI、计划任务三种方式启动 PCLauncher 结果一致，说明与本工具无关。

## 持续集成 / 发布

`.github/workflows/build-release.yml`：

- **push / PR / 手动触发**：Windows runner 上跑 `build/build.ps1` 构建单文件 exe，校验大小、内嵌资源（0Harmony / Mono.Cecil / rules）、`requireAdministrator` 清单，并执行 `--selftest`；
- **打 tag（`v*`）**：自动创建 GitHub Release，附上 `PCL-MCDownload-fixer.exe`（Release 说明里带 SHA256）。

发布流程：`git tag v1.0.0 && git push origin v1.0.0`。
