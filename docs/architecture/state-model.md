# 状态与收敛 contract

## 术语

| 对象 | 最小语义字段与保证 |
|---|---|
| ResourceKey | CapabilityId + 稳定资源标识 + 机器/用户/会话 scope；禁止仅用 PID 表示进程实例 |
| ObservedState | key、typed value、quality（Known/Unknown/Stale/Unavailable）、source epoch/sequence、Engine revision、observedAt |
| DesiredState | key、typed target、owner/rule id、priority、generation、有效期/撤销条件 |
| Intent | 经规则产生但尚未仲裁的候选目标或一次性命令 |
| Command | commandId、CapabilityId、typed args、scope、principal、deadline、幂等性声明；一次性请求 |
| Outcome | Accepted/Rejected/Pending/Succeeded/Failed/Unknown 等明确结果、原因、correlationId |

字段是语义 contract，C# 类型签名由首个计划设计。所有值带 schema/version；未知不能默认为 false、0 或空字符串。相同 epoch 内拒绝倒退 sequence；source 重启须重新握手和快照，旧 epoch 更新丢弃。Engine 为接受的变化分配顺序，来源时间戳不作为跨来源可靠全序。

仍持有的 source 重握手不能改变资源集合。适配器结束订阅后可显式 `ReleaseSource`：发布失效、取消依赖中的未执行动作，释放 source/资源所有权和容量；随后同名 source 可声明新的集合，获全局递增 epoch 并必须提交完整快照。旧 epoch 的更新或释放均拒绝，不能影响新所有者；重连快照不重放 Command。

## Command 与 Desired State

Command 表达“做一次”，例如用户明确请求的一次动作；不能为了达到某个状态而无限重放。Desired State 表达“在条件有效期间保持一个值”，例如期望显示设置；比较 Observed，只有不一致且可执行时才 apply。Setting 型 Capability 必须用绝对值而非 toggle，重复 apply 应幂等。

请求被接受不等于实际状态成功。Provider 的返回与下一次有效观测共同确认；不能写入 Desired 后假装 Observed 已变更。对无观测能力的 Command 只能报告执行回执，不伪造系统状态。

## 收敛循环（事件驱动）

1. 从一致的 revision 快照读取受影响资源；规则输出纯数据 Intent。
2. 过滤过期、模式暂停和被接管的候选；按冲突策略得到单一有效目标。
3. Known 状态已满足目标则无操作；Unknown/Unavailable 时请求按需刷新或等待，不能盲目写入。
4. 对同一资源最多一个在途动作；执行前复核 generation/授权/override。
5. 结果与当前 generation 不匹配时不得确认新目标；记录旧动作实际副作用，重新观测后决定下一步。
6. 暂时失败按有上限指数退避；永久权限/能力错误进入受阻状态，等配置或环境变化再恢复。只调度下一次有意义的 deadline。

规则失效/删除或接管后撤销其 Intent；没有胜者时默认停止管理并保留当前系统状态，**不隐式恢复“最初值”**。需恢复的行为必须是单独、明确的用户策略。

SQLite 保存意图与配置，不作为当前物理状态真相。重启将缓存观测标记 Unknown，重建必要快照，再收敛；不自动重放非幂等 Command。跨崩溃“恰好一次”不作承诺，回执不确定时呈现 Unknown，见 [persistence](persistence.md)。
