# 通用任务接入 contract

2026-10-07，决策见 [ADR 0008](../adr/0008-task-capability-composition.md)。

## 配置到执行

`AutomationTask` 包含 ID、乐观版本、名称、暂停状态和两个 `TaskInvocation`：Detection 与 Action。每个调用保存稳定能力 ID、schema 版本及参数，不含 UI 控件或平台对象。`TaskCapability` 描述类别、名称、字段、默认值及运行/编辑提示。

`ITaskDetectionCompiler` 与 `ITaskActionCompiler` 独立注册；`AutomationTaskCompiler` 验证 schema、字段、类型和值，编译为已有 `RuleCondition` / `RuleAction`。Command 与 Desired 由动作编译器明确选择。描述/编译不启动观察器、不执行副作用。

检测编译器可覆盖带 `taskId` 的 `Compile` 重载，以声明任务独立的观察资源；默认重载仍调用原纯编译方法，普通能力无需修改。启动检测需要该标识，不能把内部任务 ID 加入用户参数或依赖 UI 特例。

`IAutomationTaskBackend` 声明实际支持的 `TaskCombination`，负责该组合的读取、持久化、版本检查、运行生命周期和删除；`AutomationTaskRouter` 拒绝未接通及重复组合。宿主注册适配器，不能因为两个描述存在就假定它们能够组合执行。跨不同后端修改现有任务目前拒绝，用户需新建；不尝试跨存储非原子迁移。

WinUI 经 `AutomationProtocol` 的受权本地管道读取目录及任务，提交通用 save/delete/read/shutdown。SID 与会话来自 OS，不能相信请求中的身份；有大小、并发和期限上限。UI 不碰 Provider 或数据库，不轮询。

## 接入一种能力

1. 注册描述及纯编译器。字段支持文本、多行文本、布尔、枚举、资源、文件/目录路径、带范围的数字；`Group` 将低频参数折叠到“更多选项”。新增普通能力不修改编辑器。动态资源选项由后台读取时填充，离线原值保留；资源合法性与授权仍由实际后端确认。
2. 接通观察/执行端口、权限及生命周期，声明可执行组合。复用现有 Engine 仲裁、未知状态和事件订阅，不绕过 Engine。新增真实能力须明确通知来源和按需停止条件。
3. 新会话命令能力复用 `PluginTaskRuntime` 和 `SqliteAutomationTaskStore`；先提交再确认，保留 ID、版本、暂停状态，冲突不能静默覆盖。不自动加载外部 DLL。
4. 添加编译/拒绝/恢复测试及真实配置到执行验收。测试能力不得混入生产目录。

## 当前适配与边界

`GameHdrTaskBackend` 是首个兼容适配器，`GameHdrTasks` 映射旧 DTO，沿用旧 SQLite 数据与运行时；实际 RegisterRule 已调用通用编译器。旧 HDR 管道保留供 Agent/维护兼容。未来能力不能塞进 HDR 数据表或新增 UI 的 capability-ID 分支。

`process.running` 检测 schema 2 声明通用 `ProcessSelection` 字段，以换行文本保存最多 128 个进程名，编译成 `RuleCondition.Any`；单进程规则从旧数据库映射到同一字段。组仅提供选择来源，修改组不改已保存任务；每个适配器合计最多 128 个不同目标的限制保持。该列表属于一种检测的参数，不表示通用 AND/OR 编辑已开放。目录验证只为该字段类型允许 32768 字符，其他类型保持 4096 的上限。

`ProgramPlugin` 是第二组真实能力：`process.running` / `process.exited` 与 `program.start` / `program.close`。退出检测复用运行观察，Not(Any) 保持 Unknown；正常/强制关闭只匹配完整路径、当前用户与会话。

`AxiomStartupPlugin` 提供 `axiom.started`（schema 1）：Choice 参数 `frequency=every|firstBoot`，与程序启动/关闭组合。事件来自 Host 本身，加载时启用且版本未变的规则在 Agent 首次就绪、完整资源计划确认后收到 false → true；保留 true 让现有 Engine 支持动作延迟。UI 重开、Agent 重连、运行中新建/编辑/启用不产生启动事件；修改、暂停、删除或连接中断取消未执行延迟，不补发。

首次是当前用户本次 Windows 开机第一次启动后台，不是各条规则分别第一次执行。Host 每次启动只读取一次 Windows 注册表 BootId，并在 `tasks.db` 的 `host_startup` 单行表事务认领；库 schema 原地升级至 2，不改既有配置、不备份。即使没有启动规则也记录首次，错过/暂停/执行失败/取消/崩溃均不自动补发。BootId 或持久化不可用时首次观察为 Unknown 并记录原因，每次启动仍可触发。BootId 来源见 [微软文档](https://learn.microsoft.com/windows-hardware/design/device-experiences/oem-hvci-enablement)；本轮未实际重启电脑验证快速启动/休眠组合。

当前一次检测配一个动作；AND/OR 编辑、多步骤任务未实现。延迟、窗口状态、重复实例和关闭策略是程序能力真实参数；活动历史已持久化。测试中的会话检测/消息动作仍不代表系统会话通知功能已交付。

`WindowPlugin` 提供独立 `window.close`（schema 1），与 `process.running` / `process.exited` / `axiom.started` 组合。字段为完整 exe 路径名单、等待窗口 1–30 秒、Engine 延迟 0–86400 秒和可选标题包含过滤。Windows 端复用程序完整路径/用户/会话核对，订阅共享按需 WinEvent 后每个目标做一次初始主窗口枚举；等待 create/show 通知，无周期枚举、无注入。每次为每个目标向一个可见无 owner 主窗口发 WM_CLOSE，之后以隐藏/销毁确认，最长 3 秒；不自动处理确认框、不强杀、不重试，后续手动重开不干预。目标程序决定退出还是托盘保留，不能把成功投递消息当作窗口已经关闭。

关闭窗口和关闭程序通过通用 `ExecutableSelection` 字段支持最多 32 个目标；同一 `executable` 参数换行保存完整路径，最大 32768 字符，每项最大 1024，仍用 schema 1 兼容已有单路径，无数据库改表或写回迁移。`ExecutablePaths` 统一规范、去重及拒绝脚本/网络/Axiom 自身；启动程序仍是一个目标。单目标资源标识保持路径，多目标用排序、规范化去重路径集的 SHA256 摘要，重排不改变资源。每个批量动作仍是一个 Command，独立任务异步执行；不提供不同名单交集的事务仲裁，部分已经执行的操作不能回滚。

2026-10-06 修正跨规则阻塞：持久任务容量与运行时并发统一使用 `AutomationTaskLimits.MaximumTasks=128`。现有 Engine 的 worker 是异步任务，等待 pipe/目标/回执时不占用线程；所有支持范围内的独立规则均可等待，不排在其他规则的 30 秒等待后。延迟仍由 Engine deadline 驱动，同资源和能力声明的 single-flight、超时保留真实在途、暂停/停止取消及 Command 不重试语义不变。通用 Engine 默认并发仍是 4、显式小容量仍受限；该配置仅用于会话命令插件运行时。Agent 通信预算为 136 个客户端加一个监听实例，预留观察和当前托盘读取；无新轮询或跨规则先后顺序承诺。127 条真正在途无回执 + 第 128 条动作/暂停/安全退出回归，以及正式预览五条等待托盘规则 + 真实窗口规则通过；不代表全后台 CPU 预算已验收。

`WindowsProgramBatch` 在触发时并行启动至多 32 个独立有界等待，全部结束后汇总每个完整路径的结果；不把 30 秒等待串成多倍期限，40 秒命令期限保持。个别权限/文件/退出错误不跳过其他目标，部分失败整体为 Failed 并记录完成数及逐项原因；暂停 token 取消全部等待，已关闭的不回滚，不自动重试。正常/强制及实例范围分别应用于每个程序目标。批量能力需保存/执行/取消真正接通，不能只扩展表单。

会话 Invoke 的管道对端断开时立即取消调用 token；暂停/删除取消尚在等待的窗口动作，不能在 Host 已取消后仍等待后续窗口并关闭。已发出的关闭消息无法撤销。通知溢出/不可用或窗口仍显示返回失败/原因；命令回执不能用于推断持续后台状态。窗口通知仅用于该动作的有限等待，原进程检测继续使用原生进程事件。

`TrayPlugin` 提供 `tray.hide`（schema 1），与 `process.running` / `process.exited` / `axiom.started` 组合，声明 `TraySelection` 完整路径多选（最多 32）、等待图标 1–30 秒和 Engine 延迟 0–86400 秒。TraySelection 复用 ExecutableSelection 的名单/批量输入和校验，增加当前托盘搜索、多选、刷新面板；选择仅保存 executable 换行完整路径，旧 schema/单路径不迁移，暂无图标的原目标保留。Host 转发 Agent 的只读发现与动作共用核验，不因选取而立即执行；UI 按字段类型渲染，无具体能力 ID 分支。同一 WindowsProgramBatch 汇总逐项结果；匹配当前用户/会话/完整路径，普通 ID 核对窗口（含 message-only），除缓存 UID 外额外核验当前 HWND 数值 UID；GUID 核对系统路径且拒绝归属冲突，公开 Shell_NotifyIcon 删除并确认。注册表/Explorer 通知只在等待期间共享，末订阅释放，无轮询。一次触发移除当前可识别图标，重建后可能再出现，暂停不恢复。未知身份记录/UID 明确失败，不用折叠假成功，不注入或修改全局偏好；决策见 [ADR 0010](../adr/0010-tray-icon-discovery.md)。

## 内置插件模块接入步骤

`KeyboardPlugin` 提供 `keyboard.hotkey` 检测与 `keyboard.send` 动作，通用 `HotkeyList` 字段（枚举末尾追加，旧字段编号不变）以最多 1024 字符保存 16 组合，检测名单排序去重作为观察资源，动作按原有序序列含重复计算 SHA256 资源标识，不让不同序列长期争同一资源。检测编译为 `RuleCondition.Event`，新通知才触发；动作仍用会话 Command，延迟由 Engine 调度、短输入包声明能力级 single-flight。按键来源和发送边界见 [快捷键 contract](keyboard-shortcuts.md)。组合与字段由目录声明，WinUI 不按能力 ID 分支。单资源 Unknown 的首快照为有效状态，不整体拒绝订阅；只对失效资源停止判断，其他程序/启动规则继续可用。

1. 在 Infrastructure 实现 `IAutomationPlugin`，提供纯检测/动作编译器、支持组合和运行时 `CapabilityBinding`。可参考 `ProgramPlugin`。会话命令使用 `TaskInvocationCommands.Compile` 和其 `axiom.task-invocation` 参数 schema；动作编译器的 `Timing` 可映射到 Engine 时间策略，不能在 Provider 内 sleep 实现延迟。
2. 在 SessionAgent 实现 `ISessionCapabilityProvider`：稳定能力 ID、按需 `ObserveAsync`（首个完整快照，后续有界事件）、`InvokeAsync`（再次验证参数，再调用 Windows 适配器）。OS 代码留在 Providers.Windows。
3. 在 Host `Program` 的模块列表和 Agent `GameHdrAgent` 的 Provider 列表各登记一次。复用任务数据库、路由、权限、传输、调度和字段渲染器；无需改专用 UI、通用表结构或为能力新增 pipe 类型。
4. 明确真实事件来源、权限与故障语义；验证配置保存/读回、真实触发/动作、活动记录、暂停/退出 UI。命令结果未知时不重试。

当前通用插件运行时接受上述会话 Command contract；Desired State 的接管/恢复沿用 HDR 独立生命周期适配器。新 Desired State 场景需自己的真实恢复语义，不能伪装成命令。外部 DLL 安装、卸载、热加载、版本隔离尚未实现，不宣称是已开放的第三方插件平台。

资源计划包含动作与检测资源；仅 SessionAgent 的 Observe 资源进入会话观察流，Agent 同时核对所需会话能力 ID。只有 Host 检测时允许空观察资源，以空 snapshot 确认动作能力就绪，并持有管道等待断开，无健康轮询。Host 观察由本地 source 提供，确认订阅后再向 Engine 确认完整资源计划。连接中断、事件缺失/溢出置 Unknown 并取消未执行动作；仅有有效任务且连接失败时按五秒间隔重连，空闲不探测健康。初始/重连快照不补执行历史边沿，任务暂停/删除或条件不成立取消延迟。

插件订阅结束使用 `Engine.ReleaseSource` 显式释放资源所有权与容量，再为新资源计划握手。仍持有的 source 重握手必须保持资源集合；释放使旧 epoch 永久失效，新注册必须重新给完整快照。这解决增删规则/检测目标时沿用同一 source ID 却改变资源导致的订阅失败。

`IntervalPlugin` 的 `interval.elapsed` 是用户配置的周期检测，不是环境轮询或动作延迟：1–1440整数分钟转稳定资源，首快照计数0。同周期共享观察资源，`IntervalEventSource` 只安排所有周期最近一个到期点；以单调经过时间判断到期，每个过期周期仅发一次并从当前时间续算。取消释放定时器，重启/重连重新建立完整周期，不持久化错过次数，不唤醒电脑。提供程序、窗口、托盘、目录/网页命令组合，不默认组合键盘输入；重复动作不承诺恢复。实际注册目录进入摘要，导入再次校验整数约束。
