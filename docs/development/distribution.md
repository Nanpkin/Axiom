# 构建、清理与 Windows 分发

2026-10-07。同一份 payload 同时提供便携 ZIP 和 Windows 安装 EXE。当前准备的是预览包，不代表已发布正式第一版；不自动上传 GitHub。

## 本机打包

开发机器安装 global.json 指定的 .NET SDK（10.0.401）和 Visual Studio Build Tools 的“使用 C++ 的桌面开发”（启动器 Native AOT 编译用）。最终用户无需安装 SDK、.NET 或 Windows App Runtime。

```powershell
pwsh -NoProfile -File tools/publish-package.ps1 -Version 0.1.0-preview.1
```

输出仅在 `artifacts/release`：`Axiom-<版本>-win-x64-portable.zip`、`Axiom-<版本>-win-x64-setup.exe` 和 `SHA256SUMS.txt`。每次替换本地工作输出，不累积所有历史包；要保留正式历史版本，上传相应 GitHub Release。`-SkipInstaller` 仅用于单独检查 ZIP，不是完整分发。

启动器 Native AOT 独立 EXE；Host、Agent、WinUI 在 `app` 共享一份 .NET/WinUI 依赖。合并时相同路径必须 hash 相等，不覆盖不同版本依赖；不启用 Host/Agent/UI 的危险裁剪。WinUI 只引用实际需要的组件包，移除 umbrella 引入的 AI/ML/Widgets 等未使用组件。系统注册、快捷键重开、后台路径通过 AxiomInstallLayout 解析，保留旧预览布局兼容。

打包只有一个 `package-stage` 和一个逐项目 `package-build`；每个项目完成后复用暂存，最终删除。失败时保留 stage 供诊断，下一次重建替换；运行中的正式包不会被覆盖。发布目录不含用户库、PDB、SDK、缓存或测试夹具。Native AOT 所需编译器仅是开发依赖；Inno Setup 6.7.3 从官方已验证签名的下载安装为便携编译工具，不注册到系统。

更新开发预览仍用 `tools/publish-preview.ps1`，相同 payload 移动到唯一 `artifacts/Axiom-preview`；必须先安全停止 Axiom。此命令不自动启用登录启动。

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

## 第一次 GitHub Release

1. 满意后把源码提交到自己的 GitHub 仓库。`artifacts`、bin/obj、数据库和所有者私人手册已忽略，不提交个人配置或运行包。项目源码许可证尚未选定，由所有者在公开源码前选择，不替用户擅自指定。
2. 在本机运行上述打包命令；或仓库 **Actions → Build Windows packages → Run workflow** 填版本号，完成后下载 Axiom-windows-packages 产物并解压拿到三个文件。此工作流只构建，不自动发布 Release；本轮未在 GitHub 运行。
3. 仓库右侧 **Releases → Draft a new release**，新建标签（如 `v0.1.0-preview.1`，正式满意版可 `v1.0.0`），选择对应代码分支/提交，填写版本说明。
4. 附加便携 ZIP、安装 EXE 和 SHA256SUMS.txt。GitHub 自动附带的 Source code ZIP 是源码，不能当作软件运行包。说明下载 setup 安装，或 portable 解压。
5. 预览勾选 **Set as a pre-release**，先保存草稿核对下载和说明，再点击发布。参考 [GitHub 官方操作](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)。

此预览未购买/配置代码签名证书，安装包和启动器为未签名应用；正式对外发布时再决定签名。自带运行依赖已核对发布内容，10-09 新布局后台/UI 实际启动，进程模块核对为包内 coreclr 和 WinUI。全新 Windows/未安装任何运行时、所有 DPI、电脑重启、完整性能预算仍属于第一版发布前验收，不能用本机已装开发环境替代。部署依据：[.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[Windows App SDK 组件](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)。

两个包实际构建成功、SHA256 和内容核对通过，25 项布局/登录/协议/持久化/插件相关测试通过。10-09 用户退出游戏后，实际当前用户安装、同版本覆盖升级、开始菜单/系统卸载登记、后台/UI 启动、登录位置、快捷键重开/关闭、原生编辑与零 UI 动作通过；卸载删除本安装登录任务、文件、菜单与系统登记。精简预览已替换启动，原规则指纹与登录偏好保留。首次菜单检查使用了错误的 /GROUP 假设，实际安装组名为 Axiom；已按登记组名及快捷方式真实目标核对，产品正常。不同版本升级、可选桌面入口、下次实际 Windows 登录和干净系统另待专项验收。
