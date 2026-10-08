# ADR 0003 — 状态模型与期望状态收敛

- 状态：Accepted（用户已确定的方向；本次文档固化）
- 日期：2026-09-26
- 来源：本次明确需求；补充实现默认值见 决策状态（本机历史记录）。

## 背景

简单事件触发脚本难以表达目标持续保持、冲突和手动接管，也易重复触发副作用。

## 决策

采用 State Store + Event Bus + Rule Engine + Desired State Reconciliation。Observed 与 Desired 分离；Command 是一次性请求，不由收敛循环盲目重试。使用明确的 Override、Priority/Conflict、Debounce/Cooldown/Minimum Duration/Hysteresis，AI 不参与运行时决策。

## 替代方案与取舍

拒绝事件直接执行系统动作、将所有操作写成 toggle、将命令失败等价于可安全无限重试。

## 后果

需版本/generation、Unknown、幂等性、在途动作和恢复语义。平局不同目标报冲突；无目标时释放管理而不隐式回滚；这些补全默认值见决策状态。

## 验证

确定性重放、冲突、失效 generation、接管竞争、重启不重放命令、虚拟时间测试。见 [state-model](../architecture/state-model.md)、[rule-engine](../architecture/rule-engine.md)。

## 变更规则

改变本决策须新增 ADR 说明原因、影响与迁移，并标记 supersedes；不得静默改写本条历史。实现参数的细化不自动构成架构推翻，但必须同步相关规范与证据。
