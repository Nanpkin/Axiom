# ADR 0002 — 三进程职责架构

- 状态：Accepted（用户已确定的方向；本次文档固化）
- 日期：2026-09-26
- 来源：本次明确需求；补充实现默认值见 决策状态（本机历史记录）。

## 背景

系统能力、交互会话和 UI 生命周期不同，UI 退出不能影响自动化，也不能让高权限服务处理用户桌面。

## 决策

使用 Windows Service / Session Agent / WinUI UI 三角色。Service 为单一逻辑 Engine 和 SQLite 写入拥有者；Agent 执行对应会话观察/操作；UI 只经 Named Pipes 向 Engine 发请求。多会话可有多个 Agent，UI 可为零。

## 替代方案与取舍

拒绝 UI 单进程常驻承载全部逻辑；拒绝 Agent 各自仲裁规则；拒绝 UI 直写 Provider/数据库。

## 后果

需要身份验证、协议版本、重连/gap、进程生命周期设计；换取权限与 UI 独立性。服务身份/ACL 在宿主实现前确定，不能先用宽权限掩盖问题。

## 验证

架构测试、UI 全退出后台仍工作、多会话隔离与 Agent 重连。详见 [overview](../architecture/overview.md)、[IPC](../architecture/ipc.md)。

## 变更规则

改变本决策须新增 ADR 说明原因、影响与迁移，并标记 supersedes；不得静默改写本条历史。实现参数的细化不自动构成架构推翻，但必须同步相关规范与证据。
