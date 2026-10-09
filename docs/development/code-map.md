# 按功能定位代码

更新：2026-10-09。这里只保存入口与职责，产品进度统一见 [当前状态](../product/current-state.md)。阅读本轮相关行即可；先核对入口，必要时用限定目录的 `rg` 追踪调用方，避免全库反复摸排。

## 产品路径

`Axiom.exe → ServiceHost + SessionAgent → WinUI（可为零）`

`Workbench → WorkbenchClient → AutomationPipeServer → GameHdrTaskBackend → GameHdrRuntime → Engine → SessionAgent → Windows API`

检测与系统操作位于 Agent；规则、仲裁和授权位于 Engine/Host；UI 通过 Host 请求，不直接访问 Provider 或 SQLite。HDR 沿用兼容后端，新增程序能力走 `PluginTaskRuntime → CapabilityProviderServer → ProgramCapabilityProvider → WindowsProgramControl`。

Host 自身生命周期检测位于 Host：`AxiomStartupPlugin → PluginTaskRuntime`，仍由 Engine 调度并经 Agent 执行命令。编译器支持任务独立输入，观察订阅结束显式 `Engine.ReleaseSource`，资源变更后重新握手。

| 本轮改什么 | 先读入口，再按需追踪 | 相关规范 / 验证入口 |
|---|---|---|
| 云端环境、CI、源码隐私检查 | [云端工作流](cloud-workflow.md)、[自动检查](../../.github/workflows/check.yml)、[环境准备](../../tools/cloud-setup.sh)、[核心测试](../../tools/test-core.sh)、[隐私检查](../../tools/check-source-privacy.ps1) | 核心套件名单由 tools/core-test-suites.txt 共用；Linux 验证核心、Windows 构建完整方案；本机数据和个人现场历史排除，真实系统动作仍需本机验收 |
| 独立规则并发 / 等待不阻塞 | [PluginTaskRuntime](../../src/Axiom.ServiceHost/PluginTaskRuntime.cs)、[EngineHost.Execution](../../src/Axiom.Engine/EngineHost.Execution.cs)、[AutomationTaskLimits](../../src/Axiom.Domain/AutomationTasks.cs)、[CapabilityProviderServer](../../src/Axiom.SessionAgent/CapabilityProviderServer.cs)、[GameHdrLocalPipeFactory](../../src/Axiom.SessionAgent/GameHdrLocalPipeFactory.cs) | [任务接入](task-authoring-contracts.md)、[IPC](../architecture/ipc.md)、[PluginRuntimeTests](../../tests/Axiom.Engine.Tests/PluginRuntimeTests.cs)；128 个有界异步任务槽，136 客户端 + 监听；同资源、取消与停止沿用核心语义，无新增轮询 |
| Axiom 启动检测 / 同次开机去重 | [AxiomStartupPlugin](../../src/Axiom.Infrastructure/AxiomStartupPlugin.cs)、[PluginTaskRuntime](../../src/Axiom.ServiceHost/PluginTaskRuntime.cs)、[WindowsBootIdentity](../../src/Axiom.ServiceHost/WindowsBootIdentity.cs)、[SqliteAutomationTaskStore](../../src/Axiom.Infrastructure/SqliteAutomationTaskStore.cs) | [任务接入](task-authoring-contracts.md)、[AxiomStartupTests](../../tests/Axiom.Engine.Tests/AxiomStartupTests.cs)；Host 启动且 Agent 就绪的一次事件；`tasks.db` schema 2 全局 BootId 认领；运行中编辑/启用等下一次启动，重连不补发 |
| 启动、后台退出、发布 | [Launcher/Program](../../src/Axiom.Launcher/Program.cs)、[Host/Program](../../src/Axiom.ServiceHost/Program.cs)、[Agent/Program](../../src/Axiom.SessionAgent/Program.cs)、[发布脚本](../../tools/publish-preview.ps1) | [工作流](workflow.md)、[HDR contract](game-hdr-preview-contracts.md)；后台/UI 生命周期实际操作 |
| 分发包、安装卸载、共享路径、产物清理 | [统一打包](../../tools/publish-package.ps1)、[安全清理](../../tools/clean-artifacts.ps1)、[安装定义](../../packaging/Axiom.iss)、[AxiomInstallLayout](../../src/Axiom.Contracts/AxiomInstallLayout.cs)、[登录任务清理](../../src/Axiom.Launcher/LoginTaskCleanup.cs)、[构建工作流](../../.github/workflows/build-release.yml) | [分发说明](distribution.md)、[DistributionTests](../../tests/Axiom.Engine.Tests/DistributionTests.cs)；原生启动器 + app 共用依赖，登录/快捷键路径共用 layout，运行中拒绝安装卸载；用户数据不随包迁移或删除 |
| 启动器 Native AOT 的状态 IPC | [GameHdrProtocol](../../src/Axiom.Contracts/GameHdrProtocol.cs)、[GameHdrJsonContext](../../src/Axiom.Contracts/GameHdrJsonContext.cs) | GameHdrMessage 使用类型化 JSON 源生成，保留原消息字段与严格未知字段拒绝；DistributionTests 覆盖真实读写，不增加新协议 |
| 自动化列表、编辑、结果、设置 | [WorkbenchWindow](../../src/Axiom.WinUI/WorkbenchWindow.cs)、[Views](../../src/Axiom.WinUI/WorkbenchWindow.Views.cs)、[TaskEditorState](../../src/Axiom.WinUI/TaskEditorState.cs)、[WorkbenchClient](../../src/Axiom.WinUI/WorkbenchClient.cs) | [UI 架构](../product/workbench-ui.md)；[AutomationTaskTests](../../tests/Axiom.Engine.Tests/AutomationTaskTests.cs)、[WorkbenchPresentationTests](../../tests/Axiom.Engine.Tests/WorkbenchPresentationTests.cs)及真实窗口 |
| 进程组、任务进程选择器 | [ProcessGroups](../../src/Axiom.Contracts/ProcessGroups.cs)、[SqliteProcessGroupStore](../../src/Axiom.Infrastructure/SqliteProcessGroupStore.cs)、[WorkbenchUtilities](../../src/Axiom.ServiceHost/WorkbenchUtilities.cs)、[Groups UI](../../src/Axiom.WinUI/WorkbenchWindow.Groups.cs)、[ProcessSelectionState](../../src/Axiom.WinUI/ProcessSelectionState.cs) | `process-groups.db`；[ProcessGroupTests](../../tests/Axiom.Engine.Tests/ProcessGroupTests.cs)；组是选择来源，规则保存独立名单；由字段类型驱动，不能加 capability-ID 表单分支 |
| 组类型 / 工具入口与应用内弹窗 | [Catalog](../../src/Axiom.WinUI/WorkbenchWindow.Catalog.cs)、[Groups](../../src/Axiom.WinUI/WorkbenchWindow.Groups.cs)、[Toolbox](../../src/Axiom.WinUI/WorkbenchWindow.Toolbox.cs)、[Theme.ShowDialogAsync](../../src/Axiom.WinUI/WorkbenchWindow.Theme.cs) | 类型与管理列表分层；多行输入、Steam 设置使用 ContentDialog；批量排除与设置弹窗顺序显示，取消不改已保存值；自动化摘要在 Views 隐藏 ProcessSelection 名单 |
| Steam 启动同步 / 固定组 / 排除设置 | [SteamTool](../../src/Axiom.Contracts/SteamTool.cs)、[SteamToolManager](../../src/Axiom.Infrastructure/SteamToolManager.cs)、[SqliteProcessGroupStore](../../src/Axiom.Infrastructure/SqliteProcessGroupStore.cs)、[WorkbenchUtilities](../../src/Axiom.ServiceHost/WorkbenchUtilities.cs)、[Toolbox UI](../../src/Axiom.WinUI/WorkbenchWindow.Toolbox.cs) | [SteamToolTests](../../tests/Axiom.Engine.Tests/SteamToolTests.cs)；`process-groups.db` schema 2 原地保存开关/排除/完整结果；Host 启动一次扫描，事务更新“本机上的Steam游戏”，失败保留；规则不自动跟随组；read/save/refresh-steam-tool 受权 IPC |
| Steam 元数据发现 / 缓存 | [SteamCatalog](../../src/Axiom.Infrastructure/SteamCatalog.cs)、[SteamMetadata](../../src/Axiom.Infrastructure/SteamMetadata.cs)；Host `WorkbenchUtilities.LocateSteam` 读注册表 | [SteamCatalogTests](../../tests/Axiom.Engine.Tests/SteamCatalogTests.cs)；仅库顶层 manifest / appinfo Windows 启动项及具体文件核对，无 common 递归或轮询；非递归通知标脏；未知 exe 警告，不猜名称；不完整扫描不替换自动组 |
| 新增插件检测 / 动作能力 | [IAutomationPlugin / 编译接口](../../src/Axiom.Engine/AutomationTaskCompiler.cs)、[ProgramPlugin](../../src/Axiom.Infrastructure/ProgramPlugin.cs)、[TaskInvocationCommands](../../src/Axiom.Infrastructure/TaskInvocationCommands.cs)、[PluginTaskRuntime](../../src/Axiom.ServiceHost/PluginTaskRuntime.cs)、[会话 Provider 注册/传输](../../src/Axiom.SessionAgent/CapabilityProviderServer.cs)、[ProviderProtocol](../../src/Axiom.Contracts/ProviderProtocol.cs) | [任务接入 contract](task-authoring-contracts.md)有最小接入步骤；`ProgramPluginTests`、`PluginRuntimeTests`；HDR 生命周期仍由兼容后端承载 |
| 程序启动 / 关闭及参数 | [ProgramCapabilities](../../src/Axiom.Contracts/ProgramCapabilities.cs)、[ProgramPlugin](../../src/Axiom.Infrastructure/ProgramPlugin.cs)、[ProgramCapabilityProvider](../../src/Axiom.SessionAgent/CapabilityProviderServer.cs)、[WindowsProgramControl](../../src/Axiom.Providers.Windows/WindowsProgramControl.cs) | [Provider contract](../architecture/capability-provider.md)；[ProgramPluginTests](../../tests/Axiom.Engine.Tests/ProgramPluginTests.cs)；真实正常/强制退出需实际进程证据 |
| 关闭程序窗口 / 等待与取消 | [WindowCapabilities](../../src/Axiom.Contracts/WindowCapabilities.cs)、[WindowPlugin](../../src/Axiom.Infrastructure/WindowPlugin.cs)、[WindowCapabilityProvider](../../src/Axiom.SessionAgent/WindowCapabilityProvider.cs)、[WindowsWindowActions](../../src/Axiom.Providers.Windows/WindowsWindowActions.cs)、[WindowsWindowEvents](../../src/Axiom.Providers.Windows/WindowsWindowEvents.cs) | [任务接入](task-authoring-contracts.md)、[WindowActionTests](../../tests/Axiom.Engine.Tests/WindowActionTests.cs)；一次 WM_CLOSE、同用户/会话完整路径，按需共享 WinEvent；调用 pipe 断开由 CapabilityProviderServer 取消等待，非进程检测源 |
| 隐藏多程序托盘图标 | [TrayCapabilities](../../src/Axiom.Contracts/TrayCapabilities.cs)、[TrayPlugin](../../src/Axiom.Infrastructure/TrayPlugin.cs)、[TrayCapabilityProvider](../../src/Axiom.SessionAgent/TrayCapabilityProvider.cs)、[WindowsTrayActions](../../src/Axiom.Providers.Windows/WindowsTrayActions.cs)、[WindowsTrayEvents](../../src/Axiom.Providers.Windows/WindowsTrayEvents.cs) | [ADR 0010](../adr/0010-tray-icon-discovery.md)、[TrayActionTests](../../tests/Axiom.Engine.Tests/TrayActionTests.cs)；完整路径多选，一次删除当前 ID/GUID 图标；只读系统注册记录、含消息窗口，按需共享通知，无注入/轮询；失败不当隐藏成功，后续重建不抑制 |
| 当前托盘选择 / 动态 UID | [面板](../../src/Axiom.WinUI/WorkbenchWindow.Tray.cs)、[字段渲染](../../src/Axiom.WinUI/WorkbenchWindow.Views.cs)、[TrayCatalog](../../src/Axiom.Contracts/TrayCatalog.cs)、[Host 转发](../../src/Axiom.ServiceHost/TrayCatalogClient.cs)、[Agent 读取](../../src/Axiom.SessionAgent/CapabilityProviderServer.cs)、[活动核验](../../src/Axiom.Providers.Windows/WindowsTrayActions.cs) | TraySelection 声明驱动，打开/刷新读取、搜索/多选/取消/完整路径保存；ReadCatalog 和动作共用 FindActive，新增 HWND 数值 UID 的精确存在核验，RTSS 已实测；协议界限见 [IPC](../architecture/ipc.md)，识别限制见 ADR 0010 |
| 多目标关闭 / 完整路径批量输入 | [ExecutablePaths](../../src/Axiom.Contracts/ExecutablePaths.cs)、[WindowsProgramBatch](../../src/Axiom.Providers.Windows/WindowsProgramBatch.cs)、[ProcessSelectionState](../../src/Axiom.WinUI/ProcessSelectionState.cs)、[名单组件](../../src/Axiom.WinUI/WorkbenchWindow.Groups.cs)、[字段渲染](../../src/Axiom.WinUI/WorkbenchWindow.Views.cs) | [MultipleCloseTargetsTests](../../tests/Axiom.Engine.Tests/MultipleCloseTargetsTests.cs)；ExecutableSelection 声明驱动，32 完整路径、弹窗输入、多选、去重；旧 executable 单路径保留，多目标排序摘要，并行独立结果与取消 |
| 程序检测、HDR 执行、恢复 | [GameHdrRuntime](../../src/Axiom.ServiceHost/GameHdrRuntime.cs)、[GameHdrAgent](../../src/Axiom.SessionAgent/GameHdrAgent.cs)、[WindowsGameProcessObserver](../../src/Axiom.Providers.Windows/WindowsGameProcessObserver.cs)、[WindowsHdrDisplayProbe](../../src/Axiom.Providers.Windows/WindowsHdrDisplayProbe.cs) | [HDR contract](game-hdr-preview-contracts.md)；[AutomationRegressionTests](../../tests/Axiom.Engine.Tests/AutomationRegressionTests.cs)，真实检测和 HDR 读回不可由 fake 代替 |
| 规则保存、配置回退、数据位置 | [AxiomUserData](../../src/Axiom.Contracts/AxiomUserData.cs)、[RuleDraftDataPath](../../src/Axiom.ServiceHost/RuleDraftDataPath.cs)、[SqliteGameHdrStore](../../src/Axiom.Infrastructure/SqliteGameHdrStore.cs)、[通用任务存储](../../src/Axiom.Infrastructure/SqliteAutomationTaskStore.cs) | [持久化边界](../architecture/persistence.md)；`GameHdrPersistenceTests`、`ProgramPluginTests`。`tasks.db` 保存会话命令插件任务，`SqliteRuleDraftStore` 仍是旧文字草稿 |
| 快捷键与新功能入口 | [KeyboardShortcuts](../../src/Axiom.Contracts/KeyboardShortcuts.cs)、[ShortcutCoordinator](../../src/Axiom.ServiceHost/ShortcutCoordinator.cs)、[ShortcutAgent](../../src/Axiom.SessionAgent/ShortcutAgent.cs)、[ShortcutActionRegistry](../../src/Axiom.SessionAgent/ShortcutActionRegistry.cs)、[WorkbenchWindow.Shortcuts](../../src/Axiom.WinUI/WorkbenchWindow.Shortcuts.cs) | [快捷键 contract](keyboard-shortcuts.md)；[KeyboardShortcutTests](../../tests/Axiom.Engine.Tests/KeyboardShortcutTests.cs)；存储在 `SqliteShortcutStore`，真实注册在 `WindowsGlobalHotkeys` |
| 原生进程创建、共享监听、精确退出 | [WindowsProcessStartEvents](../../src/Axiom.Providers.Windows/WindowsProcessStartEvents.cs)、[WindowsGameProcessObserver](../../src/Axiom.Providers.Windows/WindowsGameProcessObserver.cs)、[GameHdrAgent](../../src/Axiom.SessionAgent/GameHdrAgent.cs)、[ProgramCapabilityProvider](../../src/Axiom.SessionAgent/CapabilityProviderServer.cs) | [ADR 0009](../adr/0009-native-process-events-elevation.md)；管理员 WMI 原生外部事件，不使用 WITHIN / 窗口 / 定时枚举；`--probe-process` / `--watch-process`，无消费者注销 |
| 管理员启动 / 登录启动 | [LoginStartupRegistration](../../src/Axiom.ServiceHost/LoginStartupRegistration.cs)、[AutomationPipeServer](../../src/Axiom.ServiceHost/AutomationPipeServer.cs)、[Launcher/Program](../../src/Axiom.Launcher/Program.cs) | [LoginStartupTests](../../tests/Axiom.Engine.Tests/LoginStartupTests.cs)，同用户 UAC、最高权限交互登录任务；实际登录验收未完成 |
| 活动记录、日志持久化、七天清理 | [EngineHost.Activity](../../src/Axiom.Engine/EngineHost.Activity.cs)、[ActivityJournal](../../src/Axiom.Infrastructure/ActivityJournal.cs)、[SqliteActivityStore](../../src/Axiom.Infrastructure/SqliteActivityStore.cs)、[WorkbenchWindow.Activity](../../src/Axiom.WinUI/WorkbenchWindow.Activity.cs)；现有 `AutomationPipeServer` 受权读取/清理，`GameHdrRuntime` 记录真实状态 | [持久化边界](../architecture/persistence.md)、[UI 架构](../product/workbench-ui.md)、[ActivityHistoryTests](../../tests/Axiom.Engine.Tests/ActivityHistoryTests.cs)；按窗口可视数量游标分页，后台计算七天期限，无刷新轮询 |
| 主题、标题栏、图标 | [WorkbenchWindow.Theme](../../src/Axiom.WinUI/WorkbenchWindow.Theme.cs)、[UiThemePreference](../../src/Axiom.WinUI/UiThemePreference.cs)、[WorkbenchWindow](../../src/Axiom.WinUI/WorkbenchWindow.cs)、[图标生成](../../tools/build-icon.ps1) | [UI 架构](../product/workbench-ui.md)；真窗口切换、弹窗及重开；资产目录 `assets` |
| 核心规则、冲突、时间、生命周期 | `src/Axiom.Engine/EngineHost.*.cs`（按职责拆分）、`RuleEvaluator`、`Arbitration`、`StateStore`、`EventBus`；`src/Axiom.Infrastructure/RuntimeTime.cs`、`ControlledRuntimeHost.cs` | 先按 [架构地图](../../ARCHITECTURE.md) 选相关 contract；测试在 `tests/Axiom.Engine.Tests`，按 Rule/Execution/Mode/Runtime 等类筛选，不为小改动默认全跑 |
| IPC、身份、权限 | [LocalUserPipe](../../src/Axiom.Contracts/LocalUserPipe.cs)、[AutomationPipeServer](../../src/Axiom.ServiceHost/AutomationPipeServer.cs)、[LocalPipeFactory](../../src/Axiom.ServiceHost/LocalPipeFactory.cs)、[GameHdrPipePeer](../../src/Axiom.SessionAgent/GameHdrPipePeer.cs)、[GameHdrLocalPipeFactory](../../src/Axiom.SessionAgent/GameHdrLocalPipeFactory.cs) | [IPC](../architecture/ipc.md)、[安全边界](../architecture/security.md)、`LocalUserPipeTests`、`ArchitectureTests`；显式用户所有者和实际 User SID 核对，避免 UAC 默认 Owner 差异 |

## 容易读错的入口

快捷键规则入口：[HotkeyCapabilities](../../src/Axiom.Contracts/HotkeyCapabilities.cs) → [KeyboardPlugin](../../src/Axiom.Infrastructure/KeyboardPlugin.cs) → 既有 PluginTaskRuntime → [KeyboardCapabilityProvider](../../src/Axiom.SessionAgent/KeyboardCapabilityProvider.cs)。登记共用 [HotkeyRegistrationHub](../../src/Axiom.SessionAgent/HotkeyRegistrationHub.cs) 与 ShortcutAgent 的唯一消息线程，发送在 [WindowsKeyboardInput](../../src/Axiom.Providers.Windows/WindowsKeyboardInput.cs)；表单组件 [WorkbenchWindow.Keyboard](../../src/Axiom.WinUI/WorkbenchWindow.Keyboard.cs) 由 HotkeyList 字段驱动。事件游标/消费在 EngineHost.Rules / Execution，测试 [HotkeyCapabilityTests](../../tests/Axiom.Engine.Tests/HotkeyCapabilityTests.cs) 及 PluginRuntimeTests。完整行为边界见 [快捷键 contract](keyboard-shortcuts.md)，不用重读 HDR 或另写监听线程。

- `App.xaml.cs` 默认打开 Workbench。`MainWindow` / `RuleDraftWindow` / `GameHdrWindow` 是旧界面，不是当前产品工作台；旧服务状态与文字草稿只在维护对应功能时读。
- Host 承担 Service 角色，但不是已注册的 Windows Service；不能按文件名推断安装、提权或多会话已经完成。
- 用户数据目录沿用 `preview-rule-drafts` 名称，内部同时有当前规则、快捷键和旧草稿数据库；不要因名字误读为“当前规则仍只是草稿”。
- 历史脚本、旧输出目录、旧计划中的启动方法或预设不能覆盖当前产品入口与用户数据。

## 用最少上下文完成任务

先读当前交接和本轮相关入口，沿真实调用链补读必要实现/测试。变更前核对当前文件；不因摘要而跳过授权或关键语义检查。文档维护不启动应用、不重跑功能测试；代码行为变更按直接风险验证，真实 UI / 系统动作需相应实际证据。实现职责或文件名变化后更新本表，无需逐行复制代码说明。

## 主动观察入口

`WorkbenchWindow.Usage → AutomationProtocol usage → UsageObservationController → ProviderProtocol observe-usage → UsageObservationStream → WindowsUsageObservation`。前台 WinEvent 与共享进程启动通知；UI 租约和绝对截止时间同时约束生命周期。核心对象/请求在 `Contracts/UsageObservation.cs`，有界聚合和 SQLite 在 `Infrastructure/UsageJournal.cs`，统计/脱敏/导入/扩展任务在 `UsageAnalysis.cs`。测试 `UsageObservationTests`，行为见 [使用观察](usage-observation.md)。窗口整理通过原 WindowPlugin/Provider，声明 `window.state`；测试 `WindowStateTests`。

系统会话/供电通知：`SystemEventCapabilities → SystemEventPlugin → SystemEventCapabilityProvider → WindowsSessionEvents`。同一共享订阅亦供主动观察使用，零消费者关闭隐藏消息窗口，无轮询；初始状态不补事件。测试 `SystemEventTests`。

目录/网页动作：`OpenTargetCapabilities → OpenTargetPlugin → OpenTargetCapabilityProvider → WindowsOpenTarget`。仅目录或 http/https，Engine 延迟与取消，系统接受请求即返回明确回执；不能当页面加载确认。`OpenTargetTests` 验证协议/路径/编译。网络事件加入 `WindowsSessionEvents` 的按需 NetworkChange 订阅，不收集地址。

前台应用：`ForegroundCapabilities → ForegroundPlugin → ForegroundCapabilityProvider → WindowsForegroundEvents/WindowsForegroundIdentity`。前台通知由规则与主动观察共享，规则事件唤醒后核验当前对象，Unknown 不当 false；先发布新的 true 再发布旧的 false，保持多选 Any 条件在组内切换时不误触发。`ForegroundTests` 和 Windows 共享租约回归覆盖对应边界。

- 窗口出现检测：`WindowPresenceCapabilities` / `WindowPresencePlugin` / `WindowPresenceCapabilityProvider`；`WindowsWindowPresence` 初始枚举后接共享 `WindowsWindowEvents`，`WindowPresenceState` 有界聚合。`WindowsProcessIdentity` 复用进程路径/SID核验；纯逻辑 `WindowPresenceTests`，原生回归 `WindowActionTests`。

- 使用观察的进程退出：`WindowsUsageObservation` 核验新进程后交 `ObservedProcessExitTracker` 持有有界等待句柄；暂停/退出全释放。`UsageOptions.ProcessExits` 显式范围，`UsageAnalysis` 退出对生成正常关闭暂停草稿；`ObservedProcessExitTests`、`UsageObservationTests` 验证边界。

- 窗口状态观察：`WindowsUsageWindows` / `UsageWindowHistory` 接共享窗口源，仅状态变化记录；`UsageOptions.WindowStates` 显式范围，`UsageAnalysis.WindowPairs` 经证据形成暂停草稿。`UsageWindowTests` 和原生 `WindowActionTests` 验证基线、去重、容量及真实状态。

常用场景：`AutomationTemplates` 从当前能力目录组合暂停草稿，`WorkbenchWindow.Templates` 选择并进入通用编辑器；`AutomationTemplateTests` 用实际插件注册验证补齐对象后可编译，缺能力/组合/枚举项时不展示。

低电量事件：`WindowsSessionEvents` 注册/注销电量百分比系统通知；`BatteryThresholdEvents` 只从连续已知读数发出20%/10%向下穿越。复用 `SystemEventCapabilities`、观察与建议链，`BatteryThresholdTests` 验证未知/重启基线/重复/跨阈值，`WindowsSessionEventTests` 合成原生电量消息验证共享投递。

窗口消失：`WindowAbsenceTaskDetection` 将用户的“全部消失”配置编译为现有 `window.visible` 资源的全否条件，不另开原生订阅；`WindowPresenceTests` 覆盖多选/未知及真实Engine的首次、重连、暂停、恢复边沿。

周期计时：`IntervalCapabilities` 参数与整数校验，`IntervalPlugin` 事件编译和组合，`IntervalCapabilityProvider` 首快照零计数与会话事件，`IntervalEventSource` 单调时钟/最近到期单计时器/过期合并/取消；`IntervalTests` 用可控时钟验证共享、跳时及释放。计时检测不是动作延迟，后者仍由Engine负责。

显示模式变化：共享 `WindowsSessionEvents` 接收 `WM_DISPLAYCHANGE` 但不保留载荷，复用系统事件能力和主动范围；`UsageAnalysis.DisplayLayoutPairs` 提取每次事件后同应用首个近似布局，`Draft` 将系统事件与窗口状态组合；合成原生通知与证据/暂停草稿回归分开验证。

USB类别通知：`WindowsSessionEvents` 注册USB接口GUID、处理接入/移除头并注销句柄，不解析设备名称字段；经系统检测及主动观察进入 `UsageAnalysis.SystemPairs`，保留任意设备/重复通知限制。`WindowsSessionEventTests` 的合成通知验证共享投递，不模拟物理插拔。

默认音频：`AudioCapabilities`/`AudioPlugin` 声明与事件编译，`AudioCapabilityProvider` 计数/Unknown隔离；`WindowsAudioEvents` 的MTA COM通知注册与共享租约，不读设备标识；`UsageOptions.AudioEvents` 独立默认关闭，复用观察/证据/导入链。`SessionCapabilityProvider.cs` 承载原有Provider接口和更新类型，服务器职责不变，便于验证音频失败不结束其他检测。

日历定时：`DailyTimeCapabilities` 声明、星期掩码和DST计算，`DailyTimePlugin` 编译，Agent 的 `DailyTimeCapabilityProvider`/`DailyEventSource` 管理最近deadline和失败隔离；`WindowsSessionEvents(clockOnly: true)` 仅交付时钟/设置/恢复信号，不积累普通系统事件。`DailyTimeTests` 验证合成时区与可控时钟，`WindowsSessionEventTests` 验证真实隐藏窗口消息与租约。

单次日期：`OnceTimeCapabilities` 校验真实日期/资源与DST，`OnceTimePlugin` 编译，Agent `OnceTimeCapabilityProvider`/`OnceEventSource` 共享时钟通知和最近deadline；过期/已到期状态由Host转为可读检测原因。`OnceTimeTests` 覆盖时区重算、远期分块、终止不重发与取消；导入继续强制校验后才提供暂停草稿。

播放音量：`AudioPlaybackCapabilities` 参数/边界，`AudioPlaybackPlugin` 共用默认播放资源仲裁，`AudioPlaybackCapabilityProvider` 调用 `WindowsAudioPlayback` 的Core Audio COM适配。`AudioPlaybackOperation` 只写所选字段并读回；`BoundedNativeCommand` 以单个准入和专用后台线程保护原生阻塞，避免占用公共线程池，调用者超时不提前释放准入。`AudioPlaybackTests` 为合成端点逻辑，不替代真实设备验收。

通信麦克风：`MicrophoneCapabilities`/`MicrophonePlugin`/`MicrophoneCapabilityProvider`，资源与播放分开，沿用 `WindowsAudioPlayback.SetMicrophoneAsync` 的单个原生入口、eCapture/eCommunications默认角色及立即静音读回；不改输入音量。合成逻辑与导入验证位于 `AudioPlaybackTests`/`UsageObservationTests`。

环境布局证据由 `UsageAnalysis.EnvironmentLayoutPairs` 按六类通知与目标/布局分组，处理边界与首个布局去重；草稿继续走实际系统/音频检测和窗口动作。`UsageObservationTests.RepeatedEnvironmentPreparationOffersWindowLayoutWithSeparateEventEvidence` 覆盖分类、混合不足、Axiom来源与边界中断。

文件到达：`FileArrivalCapabilities`/`FileArrivalPlugin` 声明与事件编译，`FileArrivalCapabilityProvider` 提供计数和独立Unknown隔离；`FileArrivalSource` 按目录共享 `FileSystemWatcher`，只开FileName通知、非递归、固定一秒事件批次，无扫描或内容读取。`FileArrivalTests` 覆盖真实临时目录新建/重命名、按类型匹配、初始不补发、取消释放和16组合上限。

完整插件注册入口为 `Infrastructure/BuiltInAutomationPlugins`，Host组合根与模板/导出检查共用；HDR仍走独立生命周期。完整摘要回归另并入HDR和合成设备选择，验证64 KiB、匿名字典上限与完整参数/组合保留。扩展任务包同时指向此入口和Agent注册。

桌面提醒：`NotificationCapabilities` / `NotificationPlugin` / `NotificationCapabilityProvider` 使用通用动作链；`WindowsDesktopNotification` 以消息窗口、原生清理计时器和临时通知区图标完成一次请求，`BoundedNativeCommand` 拒绝并发且调用者超时不释放原生准入。`WindowsNotificationTests` 使用真实HWND和合成Shell验证生命周期，不声称真实通知显示。

工作时段间隔：`WindowedIntervalCapabilities` / `WindowedIntervalSpec` 定义星期、跨午夜、固定钟点与DST；`WindowedIntervalPlugin` 编译事件计数，`WindowedIntervalCapabilityProvider` 复用共享clockOnly租约；`WindowedIntervalEventSource` 仅一个最近deadline及有界时钟消息等待，取消全部释放。`WindowedIntervalTests` 覆盖时段边界、错过/回拨/DST/导入；它不是通用规则时间守卫。

命令时间门禁：`Domain/WeeklyTimeWindow`、`AutomationTask.TimeWindow`（schema 2）、`AutomationTaskCompiler`映射`RuleTiming`，`EngineHost.Rules/Execution`在新边沿/延迟/取队列检查；`WorkbenchWindow.TimeWindow`及`TaskEditorState`保存与编辑，`TimeWindowTests`覆盖。

应用音量：`ApplicationAudioCapabilities/Plugin/CapabilityProvider` → `WindowsAudioPlayback.Applications`，`ApplicationAudioOperation`核验/多会话/部分结果；`ApplicationAudioTests`为合成端点测试，真实播放会话单列。

保持唤醒：`KeepAwakeCapabilities/Plugin` → `PluginTaskRuntime.KeepAwake` → `SessionDesiredLeaseStream/KeepAwakeCapabilityProvider` → `WindowsPowerRequest`；Engine窄`ILeasedDesiredExecutor`通知授权收回，`KeepAwakeTests`及Windows管道`WindowsKeepAwakeRuntimeTests`验证独立请求生命周期。

Wi-Fi配置：`WifiProfileCapabilities/Plugin/CapabilityProvider` → `WindowsWifiProfiles`共享Wlan通知及`WifiProfileState`多网卡合并；`WifiProfileTests`验证已知/未知、基线、保存及隐私。`TaskField.EditAsList`由`WorkbenchWindow.NamedLists`显示有界名单弹窗，保存仍由能力校验。

电源方案：`PowerSchemeCapabilities/Plugin/CapabilityProvider` → `WindowsPowerScheme` → `PowerSchemeOperation`；只操作本机已存在标准方案，枚举、Set、Get读回，单准入5秒有界原生执行/关停Drain。`PowerSchemeTests`用合成API验证缺失、读回及阻塞取消，不替代实机方案支持性。

精确USB实例：`UsbDeviceCapabilities/Plugin/CapabilityProvider` → `WindowsUsbDevices`的SetupAPI present接口/实例查询 → `UsbDeviceObservation/UsbDeviceState`；复用`WindowsSessionEvents(usbOnly: true)`的USB类别/恢复提示和原生注册。完整实例16项`EditAsList`，presence Any / absence All false，共用bool资源；Unknown/容量/恢复不假移除或补命令。`UsbDeviceTests`覆盖规范、队列合并、故障、Engine边沿、保存/导入；`WindowsSessionEventTests`新增消费者过滤/释放和真实SetupAPI有界查询，不能代替真实插拔。

窗口移屏：`WindowMonitorCapabilities/Plugin/CapabilityProvider` → `WindowsWindowActions.MoveToMonitorAsync` → `WindowsWindowMonitor/WindowMonitorGeometry`；沿用窗口等待/恢复和完整路径/SID/会话/创建时间核验，实时主屏/上一屏/下一屏工作区中央80%，单屏切换跳过。`WindowMonitorTests`为拓扑/几何/参数纯逻辑，`WindowsWindowMonitorTests`为6项新增原生用例（f6de1bd已过CI）；与`WindowActionTests`共享原生窗口集合，不并行干扰。持久显示器身份/完整布局恢复未接通。

单次锁屏：`SessionLockCapabilities/Plugin/CapabilityProvider` → `WindowsSessionLock` → `SessionLockOperation/SessionLockStateReader`；WTSInfoEx仅读连接与锁元数据，活动未锁定会话才请求`LockWorkStation`，5秒有界单准入与ReceiptOnly回执，不重试/解锁。`SessionLockTests`覆盖合成API、WTS头解析、保存/组合/时段及有界阻塞取消；不实际锁CI桌面。注册/导入依然走现有`BuiltInAutomationPlugins`、`GameHdrAgent`与`UsageAnalysis`。

内置亮度：`BuiltInBrightnessCapabilities → BuiltInBrightnessPlugin → BuiltInBrightnessCapabilityProvider → WindowsBuiltInBrightness/BuiltInBrightnessOperation`。执行时查询活动内置连接与WMI可写接口，0–100一次设置并读回，未知身份拒绝、外接面板忽略；`BuiltInBrightnessTests`使用合成API，不写真实面板，支持性及驱动效果待实机。

默认静音状态：`EndpointMuteCapabilities → EndpointMutePlugin → EndpointMuteCapabilityProvider → WindowsEndpointMuteEvents/EndpointMuteState`。通信输入/常规播放共用按需MTA端点通知，回调后GetMute、选择变化计数保留baseline、同ID清单通知不重绑；`WindowsAudioEvents(includeEndpointState:true)`共享默认角色/清单通知。`EndpointMuteTests`验证代次/Unknown/Engine，`WindowsEndpointMuteTests`新增3项已在769e7c0 Windows CI通过，不切换或修改真实设备。

供电条件：`PowerStateCapabilities → PowerStatePlugin → PowerStateCapabilityProvider → WindowsPowerState/PowerStateObservation/PowerStateState`。`WindowsSessionEvents(powerOnly:true)`共享隐藏窗口和AC/DC、电量通知；合并后一次只读快照，ac/battery/严格battery-below条件，无电池与未知不假低电。`PowerStateTests`验证阈值、基线与Desired释放；`WindowsSessionEventTests`新增2项共享过滤/独立溢出/只读原生快照已在769e7c0 Windows CI通过，不改系统电源。

窗口状态条件：`WindowStateDetectionCapabilities → WindowStateDetectionPlugin → WindowStateDetectionCapabilityProvider → WindowsWindowState/WindowStateObservation/WindowStateProjection`。`window.state-is`与一次性`window.state`动作分开；多完整路径按任一匹配可见主窗口三值OR。`WindowsWindowEvents(requireMinimizeEvents:true)`复用共享WinEvent与追加的系统最小化开始/结束hook，追加hook失败只拒绝新检测；先订阅后一次枚举，随后只读取事件HWND。纯逻辑/Provider/Engine的`WindowStateDetectionTests`18项已过，`WindowsWindowStateTests`2项复用自有`TrayWindow`已过main Windows CI，不采标题或周期扫描；暂停的最大化保持唤醒模板与匿名摘要目录容量回归已接。

显示器数量条件：`DisplayCountCapabilities → DisplayCountPlugin → DisplayCountCapabilityProvider → WindowsDisplayCount/DisplayCountObservation/DisplayCountState`。共享`WindowsSessionEvents(displayOnly:true)`的WM_DISPLAYCHANGE/恢复提示，计数`EnumDisplayMonitors`只保留总数，不持久化ID/坐标；最多64提示/32条件，失败或0/超64为Unknown，首次/恢复baseline。`DisplayCountTests`17项及34模板校验已过；`WindowsDisplayCountTests`2项只读实际数量并验证共享过滤/释放、已过main CI，与系统原生集合串行避免consumer计数污染。

Internet条件：`InternetConnectivityCapabilities → InternetConnectivityPlugin → InternetConnectivityCapabilityProvider → WindowsInternetConnectivityEvents/InternetConnectivityObservation/InternetConnectivityState`。`network.internet-state`经共享MTA Network List Manager connection point通知后读当前Internet flags，仅报告Windows判断、不主动探测。`InternetConnectivityTests`30项合成状态/Provider/Engine回归含立即故障/失效缓存丢弃，`WindowsInternetConnectivityTests`2项真实只读订阅/共享释放已过main CI；IPv4/IPv6状态与Unknown不误作断开，初始/恢复baseline，不采网络标识。匿名摘要compact null元数据压缩在`UsageAnalysis`，完整目录往返在`UsageObservationTests`。

当前媒体动作：`CurrentMediaCapabilities`/`CurrentMediaPlugin`/`CurrentMediaCapabilityProvider` → `WindowsCurrentMedia`的公开GSMTC适配；`CurrentMediaOperation`核对当前目标/支持操作/明确no-op/单次接受结果，复用 `BoundedNativeCommand`。`CurrentMediaTests`跨平台只用合成会话；`tests/Axiom.Providers.Windows.Tests/WindowsCurrentMediaTests`为独立Windows-only真实只读API访问，不发送媒体命令。范围与权限见任务接入的“Windows当前媒体控制”。
当前会话屏幕电源：`SessionDisplayCapabilities → SessionDisplayPlugin → SessionDisplayCapabilityProvider → WindowsSessionDisplay/SessionDisplayObservation/SessionDisplayState`，共享`WindowsSessionEvents(screenPowerOnly:true)`的GUID_SESSION_DISPLAY_STATUS DWORD通知；0/1/2为关/亮/暗，无同步状态读取或轮询。挂起/恢复和控制台/远程连接切换使状态Unknown，首次或恢复报告baseline；注册失败仅拒新检测。`SessionDisplayTests`26项覆盖三态、基线、失效缓冲和Unknown先清理，`WindowsSessionDisplayTests`2项仅注入自有消息窗口验证过滤/共享释放/独立消费者溢出，不切真实屏幕，已过PR #5 Windows CI。最大目录compact可选null及默认false标记省略在`UsageAnalysis`，保留true/完整往返约束在`UsageObservationTests`。
窗口四角一次布局：`WindowCapabilities.StateDescriptor` schema1追加四角选项 →原`WindowStateTaskAction/WindowPlugin` →`WindowsWindowActions`布局创建时间核验 →`WindowsWindowLayout/WindowLayoutGeometry`工作区2×2划分与物理像素读回。`WindowStateTests`覆盖旧/新增配置及负坐标、奇数余量、最小工作区；`WindowActionTests`新增7项自有窗口原生回归（四角、取消/旧创建时间、拒绝位置后期限），已过PR #5 Windows CI，实际桌面待验。沿用原模板；主动观察三种近似布局分类不变。

单次关闭屏幕：`DisplayOffCapabilities/DisplayOffPlugin/DisplayOffCapabilityProvider` → `WindowsDisplayOff/DisplayOffOperation`。Agent所有者SID与当前token核对、复用WTSInfoEx活动会话解析，允许已锁定会话；临时自有隐藏顶层窗口交`DefWindowProc`处理`WM_SYSCOMMAND/SC_MONITORPOWER(2)`，`SendMessageTimeout`不广播、5秒有界单准入/Drain。仅回执成功，不测量或保证物理屏幕状态，不改变锁定/睡眠/电源方案；取消已发送不恢复。4项`DisplayOffTests`使用合成API覆盖注册/暂停模板/编译/导入、活动与未知/拒绝、取消和阻塞准入；不实际关闭CI屏幕。

关闭屏幕API依据：[WM_SYSCOMMAND / SC_MONITORPOWER](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand)、[DefWindowProc](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-defwindowprocw)、[SendMessageTimeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)。同线程回调不受原生timeout限制，外层有界等待不提前释放原生准入。

程序优先级：`ProgramPriorityCapabilities/ProgramPriorityPlugin/ProgramPriorityCapabilityProvider` → `WindowsProgramPriority` → `ProgramPriorityOperation`。1–32完整路径、低于正常/正常/高于正常；按同名候选一次捕获并持有句柄，完整路径/SID/会话/创建时间/存活核验，公开Get/SetPriorityClass与读回。`BoundedNativeCommand`5秒单准入、Agent关停Drain；`ProgramPriorityTests`4项合成覆盖隔离、部分/权限/未知、取消不恢复与准入生命周期，不改真实OS优先级。模板复用已有文件名Any组检测，不是逐实例启动检测；匿名导入使用观察别名解析后再次严格校验，实际插件与Agent注册已接。

麦克风静音切换：`MicrophoneToggleCapabilities/Plugin/CapabilityProvider` → `WindowsAudioPlayback.MicrophoneToggle` → `MicrophoneToggleOperation`。原`audio.microphone`schema不变，新`audio.microphone-toggle`只接受延迟；复用共享`Commands`和`ApplyEndpoint(true, ...)`执行开始时一次绑定默认通信输入，已知GetMute反向SetMute后读回，未知初始状态不写。`MicrophoneToggleTests`4项合成与已有模板检查覆盖两方向、读/写/读回故障、取消及严格导入，不改真实麦克风；暂停快捷键模板复用现有编辑器与注册。
相对播放音量：`AudioPlaybackAdjustCapabilities/Plugin/CapabilityProvider` → `WindowsAudioPlayback.Adjustment` → `AudioPlaybackAdjustOperation`。既有`WindowsAudioPlayback.ApplyEndpoint`绑定一次eRender/eConsole端点、共用Commands准入及COM清理；初始标量必须有限且0–1，±1–100百分点限幅后写一次/读回，不碰静音、不重试恢复。新能力schema独立，资源名沿用default-playback，Engine能力键分开、原生重叠拒绝。`AudioPlaybackAdjustTests`4项合成测试覆盖严格导入/注册/两个暂停模板、上下限、无效读数/读回以及取消；不调整真实设备。

匿名摘要最后级组合压缩：`UsageAnalysis.Summary`仅在原64KiB真实超限时把允许组合按检测ID分组；Prompt/omissions说明键和值。严格Import和CapabilityCatalog不变。`UsageObservationTests.FullProductionCatalogAndMaximumObjectDictionaryFitExportWithoutDeviceIdentityOrLostParameters`验证128对象/12handled/提示词/CRLF、全部字段约束及分组展开后的完整允许对；`core-test-suites.txt`登记四个现有新动作针对性套件，无新增测试框架。
当前媒体播放条件：`MediaPlaybackCapabilities/MediaPlaybackPlugin` → `MediaPlaybackCapabilityProvider` → `WindowsMediaPlayback`的公开GSMTC投影 → `MediaPlaybackHub`共享有界工作线程。CurrentSessionChanged立即失效代际；先订阅选中会话PlaybackInfoChanged再读并核验当前选择，旧会话回调不入队。末消费者取消并在原线程注销，未完成清理阻止新共享线程。`MediaPlaybackTests`6项覆盖共享/取消、旧代际、查询Unknown恢复、无会话、溢出先Unknown和Engine/严格导入/保存/暂停模板；Windows实际API与权限待验。第三批整合追加相对播放音量/麦克风切换组合，原注册/严格导入/模板/保存回归直接验证新组合，同时保持媒体控制与程序启停排除；最大47能力匿名摘要沿原分组fallback，完整机器参数与允许对往返保留。


一组工作目标：`OpenTargetBatchCapabilities/Plugin/CapabilityProvider` → `WindowsOpenTarget.Batch` → `OpenTargetBatchOperation`。按中文类型标签分行，复用`OpenTargetCapabilities.Validate`的本机路径/文档白名单/无凭据http(s)规则；最多8项、每项255字符，通用TextList/EditAsList弹窗。全部配置与可访问性/DriveType预检先完成，复用原`WindowsOpenTarget.Open`和同一Commands准入、整批8秒；逐项顺序请求，失败/取消/超时停止后续，线程安全进度只输出数量/项序号。`OpenTargetBatchTests`4项合成案例覆盖保存/导入/模板、全部预检/顺序/部分失败、取消和超时仍持有准入；不真正启动Shell，原单目标动作语义不变。


当前前台窗口布局：`ForegroundWindowLayoutCapabilities/Plugin/CapabilityProvider` → `WindowsForegroundWindowLayout` → `WindowsForegroundWindowCommand` → `ForegroundWindowLayoutOperation`。hotkey-only、布局字段，无exe或HWND输入；共享`WindowsWindowEvents(trackCaptureLifetime:true)`在捕获前订阅、一次Bind，`CapturedWindowLifetime`有界记录绑定前销毁和绑定后失效。固定HWND/PID/thread、进程句柄/创建时间/完整路径/SID/session，每次写前仍须前台；复用`RestoreNormalAsync`新增可选guard（旧默认不改）及`WindowLayoutGeometry`，3秒事件读回、`BoundedNativeCommand`5秒单准入/关停Drain。`ForegroundWindowLayoutTests`4项合成覆盖参数/严格导入、身份/前台/销毁、结果和取消，不操作真实窗口；暂停模板走已有编辑器与注册。

第四批共用注册/模板/严格导入仍在`BuiltInAutomationPlugins`、`GameHdrAgent`、`AutomationTemplates`、`UsageAnalysis`；`OpenTargetBatchTests`原注册用例追加合成SQLite保存读回，`ForegroundWindowLayoutTests`核验快捷键唯一组合及layout-only导入。`core-test-suites.txt`登记相关既有套件；当前53能力最大匿名字段/组合回归保持原64KiB及已有压缩，不新增框架。

播放静音切换：`AudioPlaybackToggleCapabilities/Plugin/CapabilityProvider` → `WindowsAudioPlayback.Toggle` → `AudioPlaybackToggleOperation`。共用现有Commands/ApplyEndpoint，执行开始绑定eRender/eConsole端点；GetMute已知→反向SetMute→GetMute，不动音量/默认设备，未知不写、取消后不恢复。独立schema，排除EndpointMute/默认设备变化组合；Agent共享音频Drain。`AudioPlaybackToggleTests`4项合成注册/严格导入/暂停SQLite保存及模板、双向、失读/写拒绝/不一致和取消检查，不实际静音。

系统提示音：`SystemSoundCapabilities/Plugin/CapabilityProvider` → `WindowsSystemSound` → `SystemSoundOperation`。公开[MessageBeep](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebeep)的MB_OK/ICONASTERISK/ICONEXCLAMATION/ICONHAND内置类型，拒绝外部文件/地址/循环/音量参数；5秒Commands单准入与Agent Drain。成功只表示请求受理，静音/主题/远程/硬件可能无声，取消不能撤回已经受理；无UI/录音/联网。`SystemSoundTests`4项合成严格注册/暂停时段模板/导入及network.category-state→sound组合、API类型/接受与拒绝、取消和阻塞准入/关停验证；不调用真实发声API。

当前前台窗口移屏：`ForegroundWindowMonitorCapabilities/Plugin/CapabilityProvider` → `WindowsForegroundWindowMonitor` → 共用`WindowsForegroundWindowCommand` → `ForegroundWindowMonitorOperation`。hotkey-only，一个monitor选项，无exe/HWND；两前台动作共享5秒单准入、捕获前`WindowsWindowEvents`/`CapturedWindowLifetime`、原固定句柄/创建时间/路径/SID/session及每次写前前台检查，worker实际结束前持有租约。复用`WindowsWindowMonitor.ReadMonitors`与`WindowMonitorGeometry`实时拓扑/中央80%，每次还原或移动前及读回时核验拓扑，单屏相邻屏no-op；主屏即使已在主屏也居中。`ForegroundWindowMonitorTests`4项直接风险测试，受提取影响的旧`ForegroundWindowLayoutTests`4项及模板2项通过，无真实窗口操作。

Windows网络分类：`NetworkCategoryCapabilities/NetworkCategoryPlugin` → `NetworkCategoryCapabilityProvider` → `WindowsNetworkCategoryEvents/NetworkCategoryObservation/NetworkCategoryState`。独立分类共享MTA、INetworkEvents连接点，added/deleted/connectivity及property的category/connection标志提示后读取最多64已连接网络分类，另1项仅检查超限；GetCategory仅保留0/1/2或Unknown，三值OR。每类独立基线避免其他类Unknown恢复压掉Known边沿；64提示失效丢缓存，最后租约注销。`NetworkCategoryTests`6项覆盖混合Unknown/无连接、基线、公开回调ABI/过滤、查询代际/溢出、Provider→Engine与Strict import/保存/暂停模板；原生API和实际类别变动待Windows。
