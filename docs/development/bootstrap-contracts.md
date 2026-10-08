# Bootstrap contract 映射与实现细化

> 时效说明：本文记录对应阶段的实现/证据；“尚未实现”“下一步”和入口步骤指当时范围。当前产品以 [当前状态](../product/current-state.md) 为准，维护该实现时仍遵守本文有效 contract。

本文件补充 0001 的 C# 边界，不改变已接受 ADR。规则、仲裁、执行、授权和真实适配器仍属后续阶段。

| 规范 | 最小类型/入口 | 正例 | 反例 |
|---|---|---|---|
| state-model / scope | ResourceScope、ResourceKey、StateValue | machine；user identity；user + session id；带 schema/version 的 bool/number/text | 空标识、负 session、Known 无值、Unknown 冒充 false |
| Command / Desired | 两个独立 sealed 类型；不同 executor 方法 | Command 有 id/principal/deadline；Desired 有 owner/priority/generation/有效期和撤销条件 | 互相转换、非幂等 timeout 自动重试（本阶段无执行器实现） |
| capability-provider | descriptor + 明确策略记录 | setting 必须绝对值且幂等；scope/位置/权限/保真/成本/生命周期显式声明 | 没有行为支持；非法 enum；零 timeout；shell 逃生能力 |
| override / mode | ManualOverride、ModeState、RuleRetention | Override 默认 until-release；KeepInLightweight 默认 true | expiry <= start；完成切换时 requested != effective |
| 时间与执行 | IEngineClock、IDeadlineScheduler、ICapabilityExecutor | 注入单调时间；只安排业务 deadline；分别执行 command/apply | 固定 tick；在构造 Engine 时启动计时器 |

## M3–M4 的边界选择

- Engine 的短同步操作在同一 gate 内提交（逻辑序列，不常驻线程）；没有内部工作队列、Task.Run 或 idle timer。调用方天然背压。订阅投递是有界内存操作，不执行用户回调。
- 来源需先握手声明完整资源集合。Engine 分配递增 epoch（SourceSession），旧会话不能自行选择/恢复 epoch。每资源只有一个活动来源；启动/停止后必须重新握手和快照。资源 identity 必须由未来适配器稳定产生；PID 本身不是进程实例身份，核心不猜测任意字符串的 OS 含义。
- sequence 是**来源级**单调整数；snapshot 是该来源声明资源的完整集合，同一个 sequence 水位。初始/重连 snapshot 可从任意非负 sequence 开始；delta 必须严格 +1。重复或倒退不接受；跳号使整个来源 Unknown，并要求新完整快照。来源掉线亦 Unknown。
- 一次快照/断流批次共用一个 Engine revision，快照读取复制为不可变集合。同值同 quality 更新推进来源水位与观测 metadata，但不分配新 revision/不投递。Unknown 必须没有值；Stale 可保留已知旧值，不能被当作 Known 使用。
- 订阅和初始快照在 Engine gate 内原子建立；消费者先使用返回快照，再读后续事件。队列默认 256（可配置正整数，上限 65,536），默认最多 128 订阅；容量是初始工程限额，需 M6 风暴实测校准，不是吞吐承诺。
- 所有订阅保守保留每次状态变化，包括 edge-sensitive；本阶段不做合并。溢出清除未消费队列并以单个 SnapshotRequired/gap 终止订阅；不静默吞边沿，停止投递直到消费者重新原子订阅/获取快照。快照只能恢复当前值，不能恢复边沿历史/连续 true。
- 关键生命周期 Stop/Disconnect 和订阅释放不经过普通数据队列；取消唤醒等待的读取。后续控制/动作队列另行设计，当前不存在这些队列。
- 依赖索引只返回受影响 ResourceKey 对应的稳定排序 RuleIds；编辑时同步删除旧依赖。它没有评估规则或产生 Intent，不能冒称完整 Rule Engine。

当前未决范围：真实来源握手与授权、跨进程序列映射、持久恢复、规则时序/仲裁、deadline 生产实现、模式切换执行。源码与测试仅实现上表和内存传输语义。

### M5 复核补充

完整快照始终建立新 revision，并发出 `SnapshotApplied`（即使值相同）；消费者必须重置边沿/连续时长历史。同值去重只适用于普通 delta。来源/资源最大 128/16,384；每规则最大 1,024 个依赖，最多 100,000 条依赖声明；超限拒绝且不截断。同来源更换资源集合在本阶段需 Engine 重启；动态资源发现将在 Provider 计划中另定。所有这些入口都是受信任进程内 API，SourceSession 不是安全凭证；跨进程身份/权限仍需未来 IPC 边界验证。
