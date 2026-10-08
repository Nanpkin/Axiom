# 0004 单服务状态显示 contract

> 时效说明：本文记录对应阶段的实现/证据；“尚未实现”“下一步”和入口步骤指当时范围。当前产品以 [当前状态](../product/current-state.md) 为准，维护该实现时仍遵守本文有效 contract。

状态：开发预览实现；真实服务状态变化和跨机器身份负例仍待专项环境。对应 0004 执行计划（本机历史记录）。

## 资源和语义

固定资源为 machine scope 的 `windows.service.status/EventLog`，服务名按大小写不敏感的白名单校验。结果 schema `windows.service.status` v1，为 `stopped`、`start-pending`、`stop-pending`、`running`、`continue-pending`、`pause-pending`、`paused` 七个字符串之一。其他 native 值、查询失败和断线均为 Unknown，不能解释成 `stopped`。最近成功查询时间是宿主接受成功样本的绝对时间。旧时间可在暂停时展示，但界面必须标注暂停。

本资源仅 Observe，执行位置属于 Service 角色。开发版 `Axiom.ServiceHost` 是普通用户手动启动的独立进程，不是已安装的 Windows Service。UI 只引用 `Axiom.Contracts`，通过 Pipe 接收 Engine 的状态；没有 Command、DesiredState、规则输入、Session Agent、SQLite 或远程入口。Engine 的显示需求计数与连接观察寿命相连，首个需求启动 Provider，最后需求释放时注销；它不伪造规则或 Override。显示通路与 0003 的严格观察水位通路分离。SCM 通知只标脏，本地 revision 只表示 Engine 接受顺序；没有 OS 事件水位、中间状态完整性或最大 OS 延迟保证。自动化规则直接拒绝此资源，未来开放输入需另定保真和新鲜度条件。

## Windows 适配器

打开本地 SCM 时只请求 `SC_MANAGER_CONNECT`，固定 `EventLog` 服务只请求 `SERVICE_QUERY_STATUS`。先注册 `SC_EVENT_STATUS_CHANGE`，再查询首次基线。回调只递增 dirty 并唤醒一个独占 worker；零 bitmask 也唤醒。查询在同一 worker 串行运行，期间 dirty 变化使下一轮再查一次；多个提示合并为 latest-value。没有逐通知任务、空闲 timer、健康扫描或失败自动重试。异常以阶段和 Win32 code 对外报告 Unknown，重试由 UI 明确触发。

停止先关闭回调入口并唤醒 worker；同一 worker 在查询结束后于回调和 Engine 锁外调用 `UnsubscribeServiceChangeNotifications`，等待其同步返回，随后释放服务和 SCM 句柄及 delegate root。调用方的五秒等待上限不解除 native 所有权；工作未真正结束前拒绝新 generation。无需求时工作线程、订阅和 native 句柄释放。计数/屏障测试证明该策略，不代表 CPU 或通知时延达标。[微软订阅 API](https://learn.microsoft.com/en-us/windows/win32/services/subscribeservicechangenotifications)、[查询 API](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex)、[注销 API](https://learn.microsoft.com/en-us/windows/win32/services/unsubscribeservicechangenotifications) 定义 native 边界。

## Pipe v1

名字为 `axiom-status-v1-` 加当前启动用户 SID（连字符换下划线）。宿主以该 SID 和 SYSTEM 的受保护 DACL 创建 Pipe，并设置 `PIPE_REJECT_REMOTE_CLIENTS`；每个请求在读入后通过 OS impersonation 复核客户端 SID，绝不信任 payload 身份。开发版只接受同一普通用户身份，未设计跨用户 Service 身份。最多八个连接，每连接一个观察需求，64 个请求后断开。长度前缀为 little-endian int32，JSON 最大 4096 bytes；每次写入最多等待五秒，慢读者断开，状态只发送最新快照/变更，没有无界 delta 队列。

请求封套有 major=1、minor=0、schema=1、`observe|pause|retry`、request ID、`machine` scope、白名单服务名及 UTC deadline（必须在当前时间后且不超过 30 秒）。未知版本、操作、scope、服务、额外状态字段、过期或超长请求均拒绝。`snapshot`/状态更新带 revision、observing、七值状态或 Unknown、最近成功时间和错误。断连释放该连接的显示需求；重连重新取快照，不能把断线期间解释为无变化。当前无写操作，也无命令去重窗口。

## 运行和验证边界

本机需要 Windows 11 x64、仓库固定 .NET 10 SDK/运行时、WinUI Windows App Runtime 1.8，以及当前账户对 `EventLog` 的查询权限。双击仓库根目录的 `Start Axiom Status.cmd`，或运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/run-status.ps1`；入口使用 Windows 自带 PowerShell，无需 `pwsh` 在 PATH 中。入口完成 locked restore/Release build，启动 UI 和独立宿主，UI 首次显示后自动请求观察。宿主控制台按 Enter 显式退出。UI 退出只释放需求，不终止宿主。Windows opt-in smoke 脚本只查询/订阅/暂停，不安装或控制系统服务。

0005 的 UI 总览与详情只消费上述 Pipe 消息：七值状态由 SCM 最近报告值决定，Unknown/暂停/断连都清除当前已知状态；暂停前的时间如果显示，必须带“暂停前记录”标记。重试为显式动作，界面没有自动轮询。UI 的文字和视觉布局不改变 Provider/Engine/IPC 语义。

真实可控状态变化通知、远程/其他用户拒绝的外部客户端负例、长期运行与数值性能仍未实测。开发预览不能被描述为完整 Windows Provider 或正式 Service 安全验收。
