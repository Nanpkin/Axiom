# 构建、清理与 Windows 分发

2026-10-09。同一份 payload 同时提供便携 ZIP 和 Windows 安装 EXE。所有者已授权按功能批次自动发布私有 GitHub 预览版；仓库保持私有，不代表正式第一版，也不自动替换所有者本机版本。

## 本机打包

开发机器安装 global.json 指定的 .NET SDK（10.0.401）和 Visual Studio Build Tools 的“使用 C++ 的桌面开发”（启动器 Native AOT 编译用）。最终用户无需安装 SDK、.NET 或 Windows App Runtime。

```powershell
pwsh -NoProfile -File tools/publish-package.ps1 -Version 0.1.0-preview.1
```

输出仅在 `artifacts/release`：`Axiom-<版本>-win-x64-portable.zip`、`Axiom-<版本>-win-x64-setup.exe` 、`BUILD-INFO.json` 和 `SHA256SUMS.txt`。每次替换本地工作输出，不累积所有历史包；要保留正式历史版本，上传相应 GitHub Release。`-SkipInstaller` 仅用于单独检查 ZIP，不是完整分发。

启动器 Native AOT 独立 EXE；Host、Agent、WinUI 在 `app` 共享一份 .NET/WinUI 依赖。合并时相同路径必须 hash 相等，不覆盖不同版本依赖；不启用 Host/Agent/UI 的危险裁剪。WinUI 只引用实际需要的组件包，移除 umbrella 引入的 AI/ML/Widgets 等未使用组件。系统注册、快捷键重开、后台路径通过 AxiomInstallLayout 解析，保留旧预览布局兼容。

打包只有一个 `package-stage` 和一个逐项目 `package-build`；每个项目完成后复用暂存，最终删除。失败时保留 stage 供诊断，下一次重建替换；运行中的正式包不会被覆盖。发布目录不含用户库、PDB、SDK、缓存或测试夹具。Native AOT 所需编译器仅是开发依赖；Inno Setup 6.7.3 从官方已验证签名的下载安装为便携编译工具，不注册到系统。

更新开发预览仍用 `tools/publish-preview.ps1`，相同 payload 移动到唯一 `artifacts/Axiom-preview`；必须先安全停止 Axiom。此命令不自动启用登录启动。

2026-10-14 所有者已授权本机开发默认交付到上述固定预览目录：每批完成相关验证后直接构建、更新并核对启动，不再要求所有者从 GitHub 下载。入口为仓库下 `artifacts/Axiom-preview/Axiom.exe`；构建时显式传入本轮 `-Version`，避免沿用脚本旧默认版本号，交付时给出本机完整入口、实际版本与 `BUILD-INFO.json` 来源。本机预览可先于云端 Release 供测试，未通过的检查必须说明，不能冒充正式发布包。云端只同步源码和发布资产，本机接续再按此流程更新本机。

核对当前运行包和活动控制后，通过应用正常安全停止流程退出旧后台；确认实际退出才能替换和启动新预览。正在受控游戏中、存在未完成恢复或无法确认安全停止时保留现场，报告更新尚未应用。预览脚本在任何 Axiom 进程仍运行时会保留构建到 `artifacts/package-stage` 的产物并拒绝替换；这只代表构建就绪，不代表已更新。继续使用同一用户数据，不迁移配置、不自动备份、不复制整套运行库到每轮版本目录。已登记登录启动仍须核对指向实际使用的固定入口，通过现有“更新启动位置”处理，不静默声称旧登记已切换。

## 安装与卸载

安装版为当前用户安装，默认 `%LOCALAPPDATA%\Programs\Axiom`，允许选位置；创建开始菜单入口，桌面快捷方式可选，系统“已安装的应用”有卸载项。应用运行仍要求同账户管理员令牌；安装不注册 Windows Service、不自动开机运行。升级使用相同 AppId 和既有安装位置。

安装/升级先检查本会话 Axiom 是否已经停止；不强杀后台，不用 Restart Manager 自动结束进程。卸载再次检查，只有停止后才删除指向本安装目录且参数严格为 `--background` 的当前用户登录任务；其他位置登记保留。登录任务读取/移除失败阻止卸载，不假报完成。用户配置/日志始终在 `%USERPROFILE%\.axiom`，卸载默认保留，安装程序不打包或复制开发者配置。便携版表示无需安装，尚非随 U 盘保存配置的模式。

切换安装位置或从便携改用安装版，应先停止旧后台，再在新版本设置中“更新启动位置”。当前未接入自动更新。

## 保持项目小

```powershell
pwsh -NoProfile -File tools/clean-artifacts.ps1
pwsh -NoProfile -File tools/clean-artifacts.ps1 -Deep
```

普通清理移除旧候选、临时发布和 smoke 的可再生成二进制，保留夹具源码/脚本、结果文本/JSON/TRX、截图、当前预览和最新分发包。Deep 再清 src/tests/benchmarks 的 bin/obj；系统有精确匹配 SDK 才清仓库重复 SDK，旧 NuGet 空目录才清。先结束正在从 bin 或测试夹具运行的进程，不在构建期间清理；打包与清理用同一文件锁互斥。每个开发项目只保留正常增量输出，不为每个验收发布整套 self-contained runtime；临时夹具优先 framework-dependent 或共用已发布依赖，用完清理二进制、保留有效证据。

tools/dotnet.ps1 优先系统 SDK，NuGet 使用标准 `%USERPROFILE%\.nuget\packages` 共用缓存，项目不再携带另一套。首次迁移旧缓存是复用依赖而非备份用户配置。

## 按功能批次发布 GitHub 预览版

1. 每批功能在 `codex/` 分支完成并 push；核对 PR 精确 head SHA 的全部检查和对应包，再由负责人合并 main。`artifacts`、bin/obj、数据库和所有者私人手册仍不提交；许可证尚未选择，公开源码另由所有者决定。
2. 等 main 对应提交的 Check source 成功，再为这个提交创建明确标签并推送，例如 `v0.2.0-preview.20261009.1`。版本来自标签去掉 `v`，不按每个小提交自动递增或发布；同日下一批使用新的末尾序号。
3. **Publish preview release** 自动确认标签 commit 属于 main，并复用 Check source 对该精确 SHA 再跑 Linux/Windows 核心、Windows 原生和完整应用构建；即使标签之前没有检查也不能绕过。随后复用 publish-package.ps1 新构建 ZIP、安装 EXE、BUILD-INFO.json 和 SHA256SUMS.txt，不下载旧 PR 或旧版本包来发布。
4. 发布前核对四文件集合、全部 SHA256、版本、源码 SHA、干净源码标志，以及 ZIP 内外来源一致。只在最终发布 job 授予 contents:write；先创建 draft，上传全部四项并核对集合和大小，再标为 GitHub pre-release。说明链接到该固定提交的功能与限制；GitHub 自动附带的 Source code ZIP 是源码，不能当作运行包。
5. 已发布标签不能覆盖或移动；修复后换新版本。失败只留下本机制标记的 draft 时，可在 **Actions → Publish preview release → Run workflow** 输入已存在标签的版本号（不含 `v`）重跑；必须还是同一 SHA，重新检查并构建所有包后才能续传和发布。其他 draft 不自动修改；普通 Build Windows packages 手动入口仍只产出 14 天保留的私有 Actions artifact。

```powershell
# 在对应 main CI 成功后，使用已核对的提交创建标签；不移动现有标签。
git tag v0.2.0-preview.20261009.1 <已验证的main提交SHA>
git push origin v0.2.0-preview.20261009.1
```

GitHub 的预览发布由明确标签触发，与普通 push/PR 检查分开。合成回归由 `tools/test-release.ps1` 在两套 CI 核心作业中运行，覆盖版本输入、标签来源、hash、脏源码和旧 ZIP 来源拒绝；29项检查包含真实resolver入口的push和dispatch两种路径，以合成main/标签核对追加输出的version、tag及精确SHA并保留已有输出。PowerShell调用AppendAllLines明确使用string[]，避免普通Object[]在实际Release入口无法匹配.NET重载。首个`.1`标签的发布在此入口失败，未创建Release或资产；修复不移动旧标签，新发布使用检查成功的main和新版本。参考 [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)。

此预览未购买/配置代码签名证书，安装包和启动器为未签名应用；正式对外发布时再决定签名。自带运行依赖已核对发布内容，10-09 新布局后台/UI 实际启动，进程模块核对为包内 coreclr 和 WinUI。全新 Windows/未安装任何运行时、所有 DPI、电脑重启、完整性能预算仍属于第一版发布前验收，不能用本机已装开发环境替代。部署依据：[.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[Windows App SDK 组件](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)。

两个包实际构建成功、SHA256 和内容核对通过，25 项布局/登录/协议/持久化/插件相关测试通过。10-09 用户退出游戏后，实际当前用户安装、同版本覆盖升级、开始菜单/系统卸载登记、后台/UI 启动、登录位置、快捷键重开/关闭、原生编辑与零 UI 动作通过；卸载删除本安装登录任务、文件、菜单与系统登记。精简预览已替换启动，原规则指纹与登录偏好保留。首次菜单检查使用了错误的 /GROUP 假设，实际安装组名为 Axiom；已按登记组名及快捷方式真实目标核对，产品正常。不同版本升级、可选桌面入口、下次实际 Windows 登录和干净系统另待专项验收。

包内与产物旁的 `BUILD-INFO.json` 记录版本、源码提交、工作区是否有未提交源码改动、目标平台和 UTC 构建时间；不含用户名、路径或配置。PR 打包检出准确的 head SHA，CI 校验源码提交且拒绝脏工作区。`SHA256SUMS.txt` 覆盖 ZIP、安装 EXE 和来源说明。源代码归档没有 Git 信息时提交号标 unknown，不伪造来源。
