# Named Pipes contract

通用任务配置已通过 `AutomationProtocol` 独立管道承载，目录、组合与版本化配置见 [任务接入 contract](../development/task-authoring-contracts.md)。旧 HDR 协议保留作适配器与 Agent 兼容，不是新增 UI 能力的扩展入口。

只使用本地 Named Pipes 连接 UI ↔ Service、Session Agent ↔ Service。Service 是唯一规则/配置入口；UI 不直接调用 Agent 执行系统操作。协议不是远程 shell。

## 身份与授权

管道 ACL 只允许所需的服务身份和明确用户；拒绝远程客户端。Service 从 OS 提供的连接身份/token 验证用户与会话，不能信任 payload 自报 SID、SessionId 或 admin 标记。每次请求按操作、scope、Capability 和角色授权，不能只在握手时授权。Agent 注册绑定已验证的会话；用户不能通过 Agent 注册请求执行机器级任意操作。

Service account、ACL 精确 SDDL、安装与身份验证 API 属后续安全里程碑，在实现前决定并做负面测试；不得以 Everyone/匿名权限作为临时可用方案。见 [security](security.md)。

当前管理员预览的 Host/Agent 本机 Pipe 明确指定 `O:用户SIDD:P(A;;GA;;;SY)(A;;GA;;;用户SID)`，客户端在发送任何帧之前用 `LocalUserPipe.ConnectForUserAsync` 核对实际 User SID。不能使用 .NET CurrentUserOnly 代替此检查：其 Windows 实现比较 WindowsIdentity.Owner，管理员令牌的默认 Owner 可是管理员组。保留远程拒绝、服务端 SID/会话/角色核对；不接受管理员组所有者作为任意用户的替代。平台相关安全传输帮助函数局限于 Contracts/Host/Agent/UI，Domain/Engine 不依赖这些实现。

## 当前预览的会话路由

同一用户的不同Windows会话可以各自启动Host/Agent。Named Pipes名称是机器范围，当前7条端点(Automation、Provider、HDR Host/Agent、Shortcuts Agent、Status、RuleDraft)统一在SID后追加`-s{实际OS会话编号}`。编号默认读取当前进程，不能从请求正文选择目标会话；ACL、实际User SID、对端会话和角色校验继续执行，命名隔离不替代鉴权。

整包更新四角色，不回退旧无会话端点、不因路由失败重发命令。数据仍共用当前用户原目录，不复制或迁移。Automation管理通道最多8个处理客户端，额外保留第9个监听实例；超额连接关闭后，后续客户端仍能正常接入。

## 消息与流量语义

消息封套含协议 major/minor、message type、request/correlation id、payload schema version、deadline、scope。length-prefixed framing 必须有最大帧大小；序列化格式和大小具体数值在 IPC 计划确定。未知 major 拒绝；minor 兼容行为有测试。拒绝未知操作、不合法字段、超限消息。

Request/Reply 负责配置与操作；Subscription/Delta 负责订阅；Snapshot + revision 负责首次同步和缺口恢复。认证后只发送授权 scope 的数据。UI 退出立即释放订阅，后台不保留 UI 计时器或渲染状态。

队列、连接数量、请求速率均有界；慢客户端背压或断开并要求 resync，不能无限增长。latest-value 可合并；命令、权限变化、关键审计不能静默丢弃。不要用频繁 heartbeat 判断活性；优先管道关闭、OS 事件和有 deadline 的在途请求。必要的特殊失活检测须记录理由并测量。

## 重连与幂等

连接中断后带上限退避，能等待服务/会话事件时优先等待。重连先校验身份和协议再获取快照，不能把丢失区间当作无变化。请求去重使用 commandId/requestId；去重窗口和耐久级别必须明确。服务崩溃后的非幂等命令结果可为 Unknown，客户端不得自动换 ID 重发。

验收：未授权用户、伪造 session、版本不兼容、畸形帧、超大帧、断连/慢读者、重复请求和 reply 丢失都必须有测试。当前预览已有下述 IPC 子集；扩展时按真实操作验证，不将全部未来协议作为功能前置。

0004 开发预览已实现固定机器服务的只读子集；身份、帧、deadline 和 latest-value 状态流的实际默认值见 [单服务显示 contract](../development/service-status-display-contracts.md)。上述通用命令/Agent 协议验收仍属后续阶段。

0006 开发预览另有独立的当前用户规则草稿 Pipe，仅支持 `list/get/save`，每请求复核 OS SID，并拒绝未知字段；帧、并发、ACL 和错误语义见 [草稿 contract](../development/rule-draft-persistence-contracts.md)。它没有命令执行或通用 Agent 协议；跨重启 request ID 去重和跨用户正式 Service 授权仍待后续设计。

0008 游戏 HDR 开发预览的宿主/Agent 两条专用本机 Pipe、会话身份、帧、限制和当前未验收边界见 [游戏 HDR contract](../development/game-hdr-preview-contracts.md)。它仅授权结构化游戏进程观察和指定显示器绝对 HDR 设置，不是通用 Agent 执行接口。

2026-10-04 新增 `ProviderProtocol` 的会话能力管道 `axiom-providers-v1-{SID}-s{SessionId}`，由 Agent Provider 注册表分发；Host 负责 Engine 授权和调度。请求 `observe`（观察资源 + 必需能力 ID）/ `invoke`（版本化调用 + commandId + deadline），观察回复包含序号、完整初始快照与后续更新；输出资源不能冒充观察资源。仅本机 OS SID/同会话 Host 可调用，Agent 再校验参数。帧上限 256 KiB、请求读期限十秒、命令期限最多四十五秒、每观察最多 128 个资源；有界队列溢出断连，Host 置 Unknown，命令不自动重试。2026-10-06 将该通道改为最多 136 个异步客户端（128 条任务 + 观察/短读取余量），原生实例上限 137 含一个监听实例，避免在途等待阻塞其他规则；其他 Agent 管道仍维持原有实例限制。当前为显式注册内置模块，不是任意代码/外部 DLL 执行入口。

工作台 `AutomationProtocol` 支持 `read-groups/save-group/delete-group/scan-steam`；2026-10-05 新增 `read-steam-tool/save-steam-settings/refresh-steam-tool`。仍经过实际 User SID 与同会话核对，不接受 UI 指定 Steam 根目录或任意扫描路径。组写入和 Steam 设置各自携带预期版本；自动组禁止手工编辑/删除，保留名不能创建为手工组。工具设置最多 128 个合法 exe 文件名排除项，不接受路径。工具结果每页 64 个进程、最多 4096，后续页必须携带同一 scanId，结果变化拒绝拼接旧页；旧 scan-steam 安装项目分页最多 40、超大内容按序列化大小缩小，帧上限 256 KiB 不增加。新字段严格限定请求类型，Agent 快捷键通道拒绝这些字段。四入口一起更新，旧严格 AutomationProtocol 客户端不能读取新增字段，诊断工具需同步重编。

读取工具结果不触发扫描；保存开启/排除项以及手动检查可扫描，启用后的后台启动独立检查一次。文件事件只标记发现缓存失效，无周期请求；来源读取失败返回错误/警告，不当空扫描成功或伪造 exe。设置持久化成功与同步成功分别展示；扫描失败保留原组及已保存设置，不自动重试。

2026-10-06 当前托盘发现：工作台 `read-tray` 经同用户/同会话 Host 转发到 Provider 管道的同名只读操作，Agent 保留 RequireHost 身份边界。回复 `tray` 携带最多 128 个 TrayCatalogEntry（完整路径、名称最多 160 字、当前图标数 1–64）与读取时间/提示；帧仍 256 KiB，读取最长十秒。不接受 UI 的路径、命令、资源列表或返回字段作为读取参数，快捷键通道拒绝该操作；动作仍独立走 Engine invoke。新增 Tray 字段为 null 时不序列化，普通请求可与旧包安全退出互通；使用新面板时四入口需一起更新。UI 关闭弹窗取消请求；每次打开/手动刷新重新核验，无后台列表轮询。

2026-10-07 键盘能力沿用 Provider 观察/执行管道及相同帧、权限、deadline，不新增 pipe 或版本。ProviderSample 可选 Reason（最多 300 字，null 不序列化）用于注册不可用原因和真实触发组合；旧严格客户端不接受非空新字段，须整包更新。首快照允许个别资源 Unknown，宿主保持已确认资源订阅、将失效状态交给 Engine，不因为一个组合占用而断开全部检测。keyboard.hotkey 的资源使用规范化组合名单、Number 表示事件计数，keyboard.send 参数走已有 TaskInvocation Command；UI 不直接发送系统按键。
