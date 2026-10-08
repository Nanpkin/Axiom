# 0002 规则运行时 contract 细化

> 时效说明：本文记录对应阶段的实现/证据；“尚未实现”“下一步”和入口步骤指当时范围。当前产品以 [当前状态](../product/current-state.md) 为准，维护该实现时仍遵守本文有效 contract。

本轮继续已接受的规则、状态、接管、轻量模式与 CPU-first contracts；不新增平台实现。用户要求暂缓数值性能实验，先通过确定性测试保证实现策略。

| 边界 | 正例 | 拒绝/保守行为 |
|---|---|---|
| 表达式 | 闭合的相等、数值 hysteresis、AND/OR/NOT；显式 schema/version 与 primitive 类型 | 非 Known、schema/类型不匹配为 Unknown；NOT Unknown 仍 Unknown；禁止任意委托表达式 |
| 规则 | 稳定 ID、递增版本、principal/scope、动作模板、priority、时序、最大输入年龄、保留策略 | 修改不接受旧版本；自动提取依赖且校验 scope/Observe 授权，不信任手写缺失依赖 |
| 候选仲裁 | 授权/可用性→接管/模式→最高 priority；同目标合并并排序 owners | 同 priority 不同值或动作种类冲突；无候选释放管理，不恢复初值 |
| 边沿 | 已可靠观察到 false 后的 true，触发单个命令；持续 true 不重发 | 初始/重连/模式恢复快照只建基线，不补发；Unknown/gap 不当成可证实的 false，不制造边沿 |
| 时间 | 单调时间；false/Unknown/gap 即刻撤销；稳定窗口后 hysteresis，再 minimum duration | debounce 不吞失活；cooldown 内边沿丢弃并解释，不积压；时间不从静态 API 获取 |
| 执行 | 有界待执行表、固定并发、每资源一个真实在途；执行前再验版本/权限/模式/接管 | 非幂等 timeout 不重试；不能取消的真实调用仍占资源，不能用逻辑 timeout 放行第二个调用 |
| 确认 | 回执与新的有效观测独立；新 generation 只由对应请求及后续观测确认 | Accepted/Pending 不是成功；旧回执记录实际结果，强制等待重新观测，不确认新目标 |
| 观察需求/模式 | 有版本的引用计数需求计划，能力依赖闭包；ack 后才声称资源生效 | stale ack 不生效；部分失败暴露实际 held/残留资源；不可取消在途保留必要依赖 |

初始最小表达式不含完整 DSL、日历时区或脚本。所有 ID 生成与时间由注入 port 提供；不在纯决策中使用隐藏随机数。配置/模式重建允许一次性处理所有规则；普通观测只评估相关规则，目标资源观测仅唤醒相关收敛。

deadline 集合以最近到期项驱动单个 scheduler lease，取消/替换不遗留旧回调；无 pending work 时不维持计时器。需要期限的输入新鲜度、动作超时、退避与 Override 到期分别有原因。生产调度器与 OS 观察者仍后置。

观察者 demand/ack 是核心与后续宿主的显式协议：先订阅、再提交完整快照；快照不是历史回放。当前 SourceSession 仍不是安全 token，跨进程身份、持久化恢复与 OS 资源释放需要后续适配器验证。本轮不声称 UI/Service/游戏场景已运行。

## 组合入口与执行确认

先 Start，注册来源与能力，调用 ConfigureAutomation 注入授权快照与命令 ID，再 SetRule，最后显式 StartExecution。规则版本在一次生命周期内严格递增，删除也保留版本墓碑。RefreshAuthorization 使旧授权决策失效。授权 port 不做 I/O 或回入 Engine；异常按拒绝处理。时间、scheduler、ID port 应快速且可靠，生产适配器仍须专项测试。

执行协调默认并发 4、待执行容量 256、最多 3 次尝试、退避 1 秒起/30 秒封顶；可配置范围分别为 1–128、1–4096、1–10。待执行按资源合并，满时明确阻止/拒绝，不建立隐式溢出队列。控制撤销直接提交，不排在普通执行队列后。executor 在 gate 外调用，每个资源的真实调用结束前不能派第二个动作。

2026-10-07 新增根 `RuleCondition.Event`：输入为 Known、schema 匹配的非负单调事件计数；首快照/重连仅建立游标，不补发。只有新计数触发一次 Command；持续计数不重复，Unknown 取消未执行延迟。延迟或同动作资源在途时新事件推进游标但丢弃、不取消既有动作也不积压；完成消费 latch 并清除候选，保留真实回执，不让旧事件阻塞其他规则。禁止 Event 作为复合条件子项或搭配 Desired；其他 Equal/Hysteresis/复合语义不变。重复触发、忙时、延迟/暂停和消费后不占资源由 HotkeyCapabilityTests 覆盖。

Accepted/Pending 保持在途；最终回执通过 ReportExecutionOutcome(resource, generation, outcome) 关联。WaitForExecutionReceiptAsync 只等待现有调用的首次回执。成功回执不写 Observed；新的有效观测才确认收敛。旧 generation、手动动作与不确定结果要求重新观测。Command 不自动重试；Transient Desired 失败才允许有限退避，永久失败/次数耗尽等待外部配置或健康变化。

WaitForIdleAsync 只等待当前队列/在途，不代表未来 deadline 已清空。StopAndWaitAsync 的调用方必须提供有期限的 cancellation token；不可取消 executor 可能长期保留真实在途。停止撤销排队和可取消工作，晚到回执不恢复自动化。

## 观察者与模式协议

GetObservationPlan / WaitForObservationPlanAsync 输出有版本的 References、Acquire/Release、Exceptions 和 Requirements（MaximumDelay / PreserveEdges）。适配器通过 AcknowledgeObservationPlan 报告实际 held 集合、错误与实际保证；保证弱于需求不能确认。过期 ack 可更新物理事实，但不能确认最新模式；失败公开残留资源。

需求包括保留规则、接管、在途动作与后续观测屏障的依赖闭包。普通事件复用配置/模式需求缓存；配置、模式或恢复可重建全部相关需求。轻量模式默认保留规则，与 priority 无关；必要但不可轻量化的能力作为显式例外。恢复仅对已释放的输入等待新完整快照，不补发暂停期间命令。

Stop 后仍须消费最终释放计划并 ack；在途依赖要等真实结束再释放。旧在途或未释放观察者存在时禁止重新 ConfigureAutomation。Override 和接受过的命令 ID 只在同一 Engine 对象内跨 Stop/Start 保留，不能宣称进程崩溃恢复完成。

## 容量与未覆盖边界

表达式最多 256 节点/16 层；每生命周期最多 4096 个规则 ID（含墓碑）、1024 个能力、单能力最多 1024 个依赖；管理资源有容量限制。命令 ID 去重表有界且不淘汰已接受 ID，耗尽显式拒绝；注入 ID 在对象存活期间必须唯一。调度项按所有者替换/取消，只有最近项持有 scheduler lease。

当前 Desired 直接比较要求参数与结果 schema/primitive 可匹配；不支持任意转换/脚本。生产调度器故障、极端时间范围、能力依赖动态变更的边界压力、观察保证实测、持久化和安全身份尚需后续集成验证。数值性能本轮未测；上述策略和 fake 测试不能替代真实系统证据。

## 0003 集成更新

以上标注“生产调度/恢复后置”的内容记录 0002 时点；0003 已实现 Infrastructure 时间适配与受控宿主集成，详见 [生命周期 contracts](runtime-lifecycle-contracts.md)。观察者仍为 fake/port；真实 Windows Provider 与耐久恢复仍未验证。

WaitForObservationPlanAsync 在 Stop 后可继续等待最终释放计划并由调用方取消，不再错误要求 running automation。StopAndWaitAsync 仍只负责 Engine worker；宿主 StopAsync 同时等待 observer、计划消费者、回调入口和故障监听退出。创建调度失败会公开 RuntimeFault 并停止 Engine；派发前失败清除未交给 executor 的占位，不能据此释放真正执行中的动作。
