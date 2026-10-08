# ADR 0005 — Provider / Capability 模型

- 状态：Accepted（用户已确定的方向；本次文档固化）
- 日期：2026-09-26
- 来源：本次明确需求；补充实现默认值见 决策状态（本机历史记录）。

## 背景

各 Windows 能力的权限、观察方法与会话要求不同，但核心规则需要稳定的系统无关语义。

## 决策

Capability 声明 typed 操作/观察、scope、权限、成本和生命周期；Provider 实现适配。Engine 只依赖 contracts/ports。UI 与未来远程入口使用同一 Engine 能力；远程不开放任意 PowerShell/CMD。

## 替代方案与取舍

拒绝 UI/规则嵌入 Win32 调用，拒绝通用 shell provider 逃生口，拒绝第一阶段就做动态插件市场。

## 后果

Descriptor、授权与可用性必须明确。昂贵 Provider 订阅按需求释放，共享依赖引用计数，不能禁用一个规则就误停另一个。

## 验证

fake executor contract tests、引用边界测试、每能力授权/生命周期/性能测试。见 [capability-provider](../architecture/capability-provider.md)。

## 变更规则

改变本决策须新增 ADR 说明原因、影响与迁移，并标记 supersedes；不得静默改写本条历史。实现参数的细化不自动构成架构推翻，但必须同步相关规范与证据。
