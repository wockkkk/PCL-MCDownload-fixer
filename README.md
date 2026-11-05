# PCL MCDownload fixer

> **编写：DeepSeek-V4.1-Flash**（AI 编码代理）
>
> 让官方 **Plain Craft Launcher 2** 里被关闭的「MC 下载」与「整合包安装」重新可用。
> 单文件、免安装、不修改官方 exe；以 GPL-3.0 发布。
> 宿主 + 运行时注入的行为模式可能被杀软误报，详见「合规与风险」。

## 0. 快速上手（三步）

1. **先整体备份 PCL 目录**（本工具不写官方 exe，但备份永远是第一步）；
2. 把 **`PCL-MCDownload-fixer.exe`（单文件）** 放进 PCL 目录（与官方 `Plain Craft Launcher 2.exe` 同级）；
3. 以后**双击 `PCL-MCDownload-fixer.exe`** 启动（会弹 UAC，点“是”——官方 PCL 本身也要求管理员）。
   不要直接双击官方 exe，否则补丁不生效。

装好之后：

- **运行时文件都在 Fixer 自己目录下的 `runtime\` 里**，不写 PCL 目录：
  - `runtime\logs\PCL-MCDownload-fixer-<年月日-时分秒>.log` —— 运行日志
  - `runtime\reports\PCL-scan-<版本>.txt / .json` —— 拦截点报告
  - `runtime\temp` —— 仅当系统临时目录不可写时才启用
- 想改行为：编辑同目录的 `rules.json`（删掉则使用内嵌默认值）
- 自检：`PCL-MCDownload-fixer.exe --selftest`；全部参数：`--help`
- 每次启动前会把官方 exe 的 SHA256 记到 `runtime\state\last-run.sha256`，下次启动自动比对并写进日志，可自证零改动

## 1. 它做什么

- **恢复 MC 下载**：下载页的版本列表恢复正常加载，可以下载原版 / Forge / Fabric 等客户端与整合包资源；
- **恢复原版游戏入口**：下载页左侧被折叠的「原版游戏」重新显示；
- **恢复整合包安装**：支持拖入整合包文件、以及「手动安装整合包」入口，自动识别 Modrinth / CurseForge / MCBBS / HMCL / MMC / 启动器包 / 通用压缩包等格式；
- **禁用官方自动更新**：避免一次更新把补丁全部覆盖（也可以在配置里关掉）。

它只做三件事：启动前读一遍官方 exe 找出拦截点 → 在 PCL 代码运行前放行这些点 → 阻止自动更新。
**官方 exe 一个字节都不改**，所以官方升级后只要拦截形态没变，本工具通常无需改版。

## 2. 使用

把 `PCL-MCDownload-fixer.exe` 放到 PCL 目录（和官方 exe 同级），以后双击它启动。

```bat
PCL-MCDownload-fixer.exe                       启动 PCL（默认补丁）
PCL-MCDownload-fixer.exe --scan                只扫描不启动，输出拦截点报告
PCL-MCDownload-fixer.exe --pcl "D:\PCL\Plain Craft Launcher 2.exe"   指定官方 exe
PCL-MCDownload-fixer.exe --out "D:\reports"   指定报告输出目录
PCL-MCDownload-fixer.exe --no-patch            不补丁，仅验证宿主能否拉起 PCL
PCL-MCDownload-fixer.exe --no-update-block     不禁用更新
PCL-MCDownload-fixer.exe --no-loader           不改下载页加载器
PCL-MCDownload-fixer.exe --no-modpack          不恢复整合包手动安装入口
PCL-MCDownload-fixer.exe --temp "D:\PCL\temp"  指定 PCL 使用的临时目录
PCL-MCDownload-fixer.exe --selftest            运行内置自检（整合包类型识别）
PCL-MCDownload-fixer.exe --diag                诊断模式：记录 PCL 权限检查的真实异常
```

## 3. 自检

不启动 PCL 也能验证核心逻辑：

```bat
PCL-MCDownload-fixer.exe --selftest
```

会用临时压缩包跑 8 个整合包类型识别用例（Modrinth / CurseForge / MCBBS / HMCL / MMC / 启动器包 / 未知 / addons 变体），全部通过返回 0。

## 4. 已知限制

- 本仓库不包含官方 PCL 程序（版权原因），需要你自备 `Plain Craft Launcher 2.exe`。
- 自动化 / 沙箱环境里 PCL 自身可能因为“无权写临时目录 / 注册表”弹「运行环境错误」；「完整下载客户端 / 安装整合包」这类 GUI 端到端验证请在正常桌面会话里完成。
- 只对**官方原版 PCL** 有效。若你用的是第三方修改版或自行编译版，拦截形态可能不同，届时日志里会出现「未识别形态」或「未匹配到伪 LoaderTask 构造」。

## 5. 建议的验收步骤

1. 备份 PCL 目录 → 双击 `PCL-MCDownload-fixer.exe`，确认主界面、主题、正版登录、启动游戏与官方一致；
2. 进「下载」页，确认版本列表能加载、能完整下载一个客户端；
3. 拖入一个 Modrinth / CurseForge 整合包，或点「手动安装整合包」，确认能安装；
4. 重启 `PCL-MCDownload-fixer`，确认补丁仍然生效；手动点「检查更新」应无反应；
5. 对比官方 exe 的 SHA256（日志里就有）。

## 6. 常见问题

**双击官方 exe 还是一样？**
补丁只在通过 `PCL-MCDownload-fixer.exe` 启动时生效，请改成双击它。

**杀毒软件报毒 / 提示行为可疑？**
宿主 + 运行时注入的形态和恶意软件相似，属于误报；本项目不会尝试绕过任何安全软件。介意的话可以自行从源码构建后再用，见 `DEVELOPMENT.md`。

**点「检查更新」没反应？**
是本工具禁用官方更新造成的，属预期行为；加 `--no-update-block` 可关闭。

**日志里出现「未识别形态」/「未匹配到伪 LoaderTask 构造」？**
说明该版本 PCL 的拦截形态变了：把 `runtime\reports\PCL-scan-*.txt` 反馈后按需更新 `rules.json` 即可，不需要改代码。

## 7. 合规与风险

- 本项目以 **GPL-3.0** 发布，**允许再分发**（保留版权与许可声明即可）。
- 宿主 + 运行时注入的行为模式与恶意软件相似，**可能被杀毒软件误报**；本项目不会尝试绕过任何安全软件。
- PCL 作者是出于合规原因主动下架这些功能的。绕过该限制可能触及当地法律法规或 PCL 的许可条款，**使用与分发的风险由你自行承担**；本 README 不构成法律意见。
- 本仓库不包含官方 `Plain Craft Launcher 2.exe`，也不修改它；其版权归 PCL 作者所有。

## 8. 许可

本项目以 **GNU General Public License v3.0** 发布，全文见 [LICENSE](LICENSE)。

- 依赖：Lib.Harmony（MIT）、Mono.Cecil（MIT），与本项目一起分发。
- 上游项目（PCL 本体）：<https://github.com/meloong-Git/PCL>
- 官方 `Plain Craft Launcher 2.exe` 不属于本项目，未修改、不随附源码，其版权归 PCL 作者所有。
- 应用图标由 PCL 官方图标**略作修改**（右下角加对勾徽标）而来，版权归 PCL 作者，仅用于视觉识别；如 PCL 作者有异议可随时替换。
- 再分发本项目的源码或编译产物时，请保留 `LICENSE` 与版权声明；官方 PCL 不在本项目授权范围内。

## 9. 从源码构建

需要 Windows + .NET Framework 4.8 + .NET SDK：

```bat
build\build.cmd
```

产物是单文件 `PCL-MCDownload-fixer\PCL-MCDownload-fixer.exe`，其版本信息（文件说明 / 产品名 / 版本 / 版权）、应用图标与 requireAdministrator 清单由 `build/version.txt` 与 `build/app.ico` 在构建时自动写入。
构建细节、补丁策略、宿主兼容层与 CI 说明见 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 10. 声明

本项目的源码（`src/`）、构建脚本（`build/`）、GitHub Actions 配置以及本文档，
均由 **DeepSeek-V4.1-Flash** 编写与整理；需求、验收与最终取舍由使用者决定。

如发现代码或文档问题，欢迎提 issue。
