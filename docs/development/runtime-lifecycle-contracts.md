# 0003 运行时适配与生命周期

> 时效说明：本文记录对应阶段的实现/证据；“尚未实现”“下一步”和入口步骤指当时范围。当前产品以 [当前状态](../product/current-state.md) 为准，维护该实现时仍遵守本文有效 contract。

沿用规则运行时和 ADR 0001–0006。本阶段只接入进程内受控观察器；没有真实 Provider、跨进程身份或持久化恢复。

## 时间与故障

Infrastructure 的时钟与 scheduler 共用一个 TimeProvider 和 timestamp 原点。单调 elapsed 用于 deadline，UTC 跳变不改变已安排的相对期限。timer 为一次性；超出底层上限时仅按剩余期限分段，不周期扫描。Dispose 不等待正在执行的回调，Engine 的版本检查负责拒绝已进入队列的旧回调。底层 TimeProvider 必须不在 CreateTimer/Change 内同步执行回调。

调度创建失败由 Engine 停止并公开故障；异步计时/回调异常由 scheduler 公开故障信号，受控宿主停止 Engine 并开始清理。不得静默失去 deadline 后继续执行。无有效工作不启用 timer。

API 依据：[TimeProvider.CreateTimer](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.createtimer?view=net-10.0) 的一次性/禁用周期语义；[ITimer](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/Threading/ITimer.cs) 的最大 4294967294 毫秒范围。分段不宣称实际精度或 OS 休眠行为已验证。

## 固定来源与观察器

一个受控 observer 对应固定资源集合和稳定 ID 前缀；不动态发现资源。宿主为每个资源建立固定单资源 Engine 来源，完整快照因此只涉及该资源。未激活资源保持 Unknown，不读取或激活它们；新增/释放无关观察器不重置保留规则的连续时长。受控宿主最多 128 个固定资源，遵守现有 Engine 来源容量，不扩大核心限制。配置回调不得另占这些来源或提前 StartExecution。

AcquireAsync 只订阅当前需要的资源并返回 lease（含保证），共享需求由资源 key 合并。取得 lease 且保证满足需求后，才发布新增/恢复资源的快照。异步 port 必须快速返回 ValueTask；不能同步阻塞等待外部通知。Acquire 失败须自行清理未交付资源，Release 失败则仍由宿主持有 lease。

观察通知携带整个 observer 激活通知流的连续 sequence；宿主先检查流连续性，再转换为各 Engine 来源的递增 sequence。Snapshot 返回同一通知流的水位及请求资源的状态。只丢弃已被相应资源快照覆盖的通知；未刷新资源的缓冲边沿仍需处理。启动/全量恢复以快照水位建立流基线，资源确认与启动执行之前不释放缓冲通知。adapter 必须提供一致水位；不能保证时发送 gap。

普通回调只做有界 TryWrite；数据消费者每批最多 32 项，随后释放宿主 gate，控制操作不排在数据队列后。断连/溢出使用独立合并标志，不能被队列容量吞掉。gap 使来源 Unknown 并暂停旧边沿，等待 Recover 或新的配置/模式需求，不因 ack 变化自动重试。Recover 模拟重连/恢复信号，重新取得必要 lease、建立 source epoch 和快照；旧 callback generation 和已释放 lease 的回调无效。

协调器串行拥有 lease 账本，物理 acquire/release 完成后才改变账本。失败公开原因并等待需求变化或 Recover；不根据失败产生的新 ack 计划立即无限重试。模式计划变化可以使旧操作过时，但其物理结果必须继续记账并对账最新需求。弱保证不确认成功。

## 宿主与停止

宿主拥有 Engine、scheduler 和 observer leases，Engine 仅借用 ports。启动配置由受信任组合回调完成；必要订阅和快照成功后才 StartExecution。停止先 Engine.Stop，再由协调器消费最终释放计划；执行 worker、observer 操作与 lease 全部结束才算清理完成。

StopAsync 必须传入正的有限 budget（默认不隐藏）；使用同一注入 TimeProvider 等待整个清理任务。超时返回 held/in-flight/error，清理任务继续等待实际完成，不把不可取消操作伪装为结束。停止不是强杀；释放失败必须报告，显式 Recover/再次停止可重试。未清理完成不允许 Start；重复停止安全。异常、停止和普通事件走独立唤醒，且不存在无界 Task.Run。

恢复仅限同进程对象与受控信号；新启动重建配置/来源，沿用核心同对象 Override/命令去重。生产 OS 恢复、崩溃耐久性、真实释放和 CPU 数值不在本阶段证据内。

正的底层 timer 等待向上取整至整毫秒，避免亚毫秒剩余期限被截断为零后反复立即重排；这是唤醒策略保证，不是精度承诺。Stop budget 最大为底层单次 timer 范围（4294967294 毫秒），且必须为正。

## 审查修复后的实现边界

受控宿主在配置前启用 Engine 观察就绪检查。快照发布在 Engine gate 内复核当前计划版本与保证；新输入等待基线时，已就绪输入仍继续消费通知。传递依赖就绪后重新评估受影响目标。释放预留与派发使用同一 Engine gate：先撤销就绪再执行物理释放，真实在途（包括手动动作）的依赖闭包必须保留到完成。

通知回调不取得宿主 gate；每代使用独立有界 mailbox 和 gap 标志，已通过入口检查的旧回调也不能污染恢复后的数据流。lease 保证在宿主 gate 外读取并缓存。部分输入的延后通知与普通队列共享容量上限，没有新增轮询。

确认阶段调度失败仍保持原故障与停止语义，worker 完成真实在途清理后正常退出。协调器主动识别 Engine 已停止，不依赖故障监听先获得执行机会；停止后断开来源是安全的空操作。scheduler 故障信号按生命周期隔离，旧 lease 的迟到异常只报告旧生命周期。Dispose 进入时即禁止 Start，清理超时后仍维持禁止启动，直到完成处置。

第二轮复审修复：快照与计划确认交接期间，同资源新通知不得越过已有 deferred；由单一 ingress 按序投递，deferred 与普通通知共享每批 32 项预算，批间释放 gate。配置变化触发的恢复也更换 mailbox 与 epoch。Command 出队取消队列期限导致调度失败时，worker 在已记录故障且 Engine 已停的条件下退出，实际资源仍按停止协议完成清理。
