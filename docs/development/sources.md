# 来源与核实范围

本文用于追溯，不要求代理每轮访问所有链接。

- 产品和架构选择：2026-09-26 用户本次明确请求；历史预览仅提供命名和开发方式背景。原历史完整内容未能读取，限制见 decision-status（本机历史记录）。
- Codex 使用说明核对：[AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md)、[worktrees](https://learn.chatgpt.com/docs/environments/git-worktrees)、[features](https://learn.chatgpt.com/docs/features)。手册中的任务拆分、模型分工和审查流程是本项目建议。
- .NET 官方：[支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)、[Windows 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。SDK 已由 global.json 固定；安装与构建证据见 0001，文档链接本身不代表本机环境可用。
- 性能工具官方：[ETW](https://learn.microsoft.com/en-us/windows/win32/etw/about-event-tracing)、[WPR/WPA](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-xperf-wpa-wpr)、[BenchmarkDotNet](https://benchmarkdotnet.org/)。

性能数字是本次拟定的 provisional engineering targets，不来自工具官方性能保证。通知 API 是否适用于具体 Provider、权限与数据完整性，留到对应实现计划验证。
