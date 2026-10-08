# Axiom

一个在 Windows 后台自动检测条件、执行任务的小工具。配置好自己的自动化后，可以关闭界面，让它继续工作。产品追求灵活配置、易于扩展和尽量少的 CPU 工作，而不是让用户理解内部引擎。

目前只是一个很小的可用阶段，后面还会持续变化；按实际需求逐步补功能，保持简单、易改。

## 现在怎么用

开发预览双击完整文件夹中的 [Axiom.exe](artifacts/Axiom-preview/Axiom.exe)。分发提供两种包：便携 ZIP 解压后运行 Axiom.exe；安装 EXE 提供开始菜单、可选桌面快捷方式和 Windows 卸载入口。两种包均包含运行依赖，不需要用户另外安装 .NET 或 Windows App Runtime；当前仍待干净 Windows 环境验收。

“新建任务”按检测类型 → 检测参数 → 动作类型 → 动作参数配置。支持程序运行/全部退出、Axiom 启动（可选本次开机首次）和全局快捷键检测；动作包括 HDR 自动恢复、启动/关闭程序、关闭窗口、隐藏托盘、模拟组合键，可用组合按能力声明显示。可保存、编辑、暂停、删除规则，多个对象在应用内弹窗选择。左侧“组”管理进程名单，“工具箱”可启用启动时更新本机 Steam 游戏组和设置排除项。新增模块接入相同编辑器，见 [任务接入](docs/development/task-authoring-contracts.md)。

打开 Axiom 时按当前账户请求管理员权限，后台和窗口继承权限。在“设置 → 登录启动”中启用后，当前用户登录计划任务以最高权限运行后台，不打开窗口、不保存密码；可随时关闭。移动整个程序文件夹后，从新位置打开并点击“更新启动位置”；删除程序前先关闭登录启动。Windows 可延迟启动；登记可在任务计划程序中检查。

“设置 → 全局快捷键”可新增、编辑、启用/停用、删除。同一个快捷键切换显示 / 关闭 Axiom 窗口，关闭时任务栏按钮一起消失；后台继续运行，再按即可打开。首次初始化默认 Ctrl+Alt+A，之后以自己的设置为准。功能目录和组合键独立，后续应用功能可沿同一入口注册，见 [快捷键 contract](docs/development/keyboard-shortcuts.md)。应用 EXE 和窗口采用统一图标与融合标题栏。

“设置 → 外观 → 主题”可选跟随系统、浅色或深色，默认跟随系统。切换立即生效并自动保存，重新打开后保留选择；弹窗和标题栏按钮一起切换。

用户数据统一保存在 `%USERPROFILE%\.axiom`，与程序发布文件分开，开发启动和登录启动访问同一份配置。程序不自动生成备份、不启用 EFS 加密。

“活动记录”保存检测、执行、恢复、异常和配置操作，可按日期查看、翻页访问更早活动，重启后台后仍保留；点击“清除 7 天前的记录”只删除超过七天的日志。活动从此版本开始记录，旧版仅在内存中的结果无法补回。

当前仍是开发预览：已在运行的游戏不会被补接管。关闭窗口保留后台，“设置 → 停止后台并退出”才停止检测，下次手动打开或登录启动时继续加载任务。正在控制 HDR 时，先正常退出游戏并等待恢复。完整现状、验证边界及下一步统一见 [当前状态](docs/product/current-state.md)。

## 开发从哪里开始

- [AGENTS.md](AGENTS.md)：后续 AI 的工作规则。
- [当前状态与交接](docs/product/current-state.md)：新对话先读的现状、验证边界与未解决事项。
- [代码定位表](docs/development/code-map.md)：按功能进入相关实现，避免全库重复摸排。
- [产品范围](docs/product/v1-scope.md)：用户价值、任务模型和交付顺序。
- [UI 架构](docs/product/workbench-ui.md)：页面、流程、文案与实现职责。
- [架构地图](ARCHITECTURE.md)：按需定位技术规范。
- [文档索引](docs/README.md)：当前规范与历史证据的区别。

0001–0015 已退出当前执行路线；旧计划仅保存历史验收和决策，不按编号继续推进，不要求新对话通读。

SDK 固定于 `global.json`。`tools/dotnet.ps1` 优先使用系统 SDK；NuGet 复用标准用户缓存，不再在项目内保存另一套。环境设置只作用于子进程。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 restore Axiom.slnx --locked-mode
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 build Axiom.slnx -c Release --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 test Axiom.slnx -c Release --no-build --no-restore
```

开发入口、旧服务观察/文字草稿工具见 [工作流](docs/development/workflow.md)。它们不是产品主入口。性能设计和测量时点以 [性能政策](docs/development/performance.md) 为准；核心微基准不代表整个产品接近零 CPU。

生成便携 ZIP、安装 EXE 和校验值：`pwsh -NoProfile -File tools/publish-package.ps1 -Version 0.1.0-preview.1`，输出在 `artifacts/release`，每次只保留最新一套。构建所需工具、清理命令及 GitHub Release 操作见 [分发说明](docs/development/distribution.md)。

重新生成开发预览：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/publish-preview.ps1`。先在设置中安全停止后台；正在游戏中时等待正常退出及 HDR 恢复。脚本替换旧预览并保留用户配置、正常依赖锁文件。
