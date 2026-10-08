# 总体架构与依赖

状态：用户选定总体架构；下述责任分配是本次奠基明确的实现约束。参见 决策状态（本机历史记录）。

## 运行时职责

| 角色 | 拥有内容 | 禁止承担 |
|---|---|---|
| Windows Service | 权威 Engine、配置、SQLite 写入、机器级操作路由 | 交互桌面 UI、假装拥有用户会话上下文 |
| Session Agent | 对应用户/会话的观察器与执行器、向 Engine 反馈状态 | 独立规则仲裁、跨用户操作、直接写权威数据库 |
| WinUI UI | 配置、解释、手动操作入口、状态订阅 | 绕过 Engine 调 Provider、读写 SQLite、常驻要求 |

一个 Service 逻辑仲裁者串行确定同一资源的决策。多个会话使用 User SID + Session ID 隔离。Agent 断开时相关 Observed State 变 Unknown/Unavailable，停止依赖有效状态的动作；重连后按需重新订阅并快照对账。Session Agent 可在所需会话自动启动；UI 从不作为其存活条件。

Service 启动先装载并验证配置、标记观测为 Unknown，建立必要订阅，再处理收敛。关停取消新工作，限时等待正在执行的动作，批量持久化；不能无限等待。系统休眠恢复只对激活的依赖补快照并重算 deadline，不全量忙循环。

## 逻辑数据流

通知 → Provider 规范化 Observed 更新 → State Store 接受版本 → Event Bus 发布变化 → 依赖索引选规则 → 计算候选 Intent → Override/模式/冲突仲裁 → Desired State 或 Command → Capability 授权 → Provider 执行 → 独立观测确认。

事件总线为有界进程内设施，Named Pipes 承担跨进程传输；不得把它们混为一个无界广播队列。事件不能直接触发绕过仲裁的系统调用。

## 项目依赖（按里程碑逐项建立）

- `Axiom.Domain`：值类型、规则/状态/Capability contracts；只依赖 BCL。
- `Axiom.Engine` → Domain：状态、事件、规则、调度、收敛及 ports；不引用 UI/Win32/SQLite/具体 Provider。
- `Axiom.Contracts` → Domain（仅必要的稳定类型）：IPC DTO/版本；与 Engine 实现解耦。
- `Axiom.Infrastructure` → Engine/Domain、Contracts：SQLite、时钟、调度等适配器；平台 API 置于专用 Windows 适配器，不污染核心。
- `Axiom.Providers.Windows` → Domain/必要 ports：系统 API 封装；每种能力声明执行位置。
- Service/SessionAgent 宿主负责组合适配器；WinUI UI 只通过 IPC 客户端访问应用操作。

只在真实场景需要时建立项目；早期核心奠基已完成，不重走先建全部底层再做功能的顺序。核心采用可注入时钟、调度器、执行器、存储接口，测试不依赖管理员权限或本机桌面。

0003 已按上述依赖方向建立 Infrastructure 的时间适配与进程内受控宿主。它消费 Engine 观察计划、协调 fake/port lease，并用有限预算管理停止与晚到清理；不等于 Windows Service 或 Session Agent 已落地。固定单资源来源避免无关观察需求变化重置保留规则；详细映射、容量与恢复边界见 [生命周期 contracts](../development/runtime-lifecycle-contracts.md)。

0004 开发预览的独立控制台宿主承担 Service 角色中的固定服务观察与 Engine 状态拥有权，WinUI 经本机 Named Pipe 访问；它不安装为 Windows Service，也不创建空 Session Agent。SCM latest-value 显示通路不进入 0003 严格水位观察协议；具体边界见 [单服务显示 contract](../development/service-status-display-contracts.md)。0006 在同一开发宿主中增加独占 SQLite 规则草稿存储与独立规则 Pipe；草稿不进入可执行规则索引，见 [草稿 contract](../development/rule-draft-persistence-contracts.md)。

当前开发预览由宿主持有 Engine 和游戏规则 SQLite，会话 Agent 持有窗口进程观察与 HDR 适配器，WinUI 经宿主管道配置/查看/暂停。Engine 负责规则、冲突、Desired State 与执行授权，UI 不直接调用 Agent。这里只记录职责，功能、登录启动与实测边界统一见 [当前状态](../product/current-state.md)；0008（本机历史记录） 已停止沿用，不是当前进度入口。
