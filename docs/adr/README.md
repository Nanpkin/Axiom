# Architecture Decision Records

ADR 记录背景、决策、替代方案、后果和验证。Accepted 表示架构方向已选定，**不表示代码已实现或性能已通过**。新增决策采用连续编号；改变既有方向时引用旧 ADR 并记录 supersedes。

| 编号 | 决策 | 状态 |
|---|---|---|
| [0001](0001-use-dotnet-10.md) | C# / .NET 10 | Accepted |
| [0002](0002-three-process-architecture.md) | 三进程职责与边界 | Accepted |
| [0003](0003-state-reconciliation.md) | 状态与收敛 | Accepted |
| [0004](0004-event-driven-cpu-first.md) | 事件驱动 CPU-first | Accepted |
| [0005](0005-provider-capability-model.md) | Provider / Capability | Accepted |
| [0006](0006-lightweight-mode-is-first-class.md) | 轻量模式一等能力 | Accepted |
| [0007](0007-game-process-window-hints.md) | 普通用户会话的游戏窗口提示与断线重连 | 到达提示/权限部分被 0009 替代；其余保留 |
| [0008](0008-task-capability-composition.md) | 通用任务、独立检测与动作编译 | Accepted |
| [0009](0009-native-process-events-elevation.md) | 管理员会话的原生进程通知 | Accepted |
| [0010](0010-tray-icon-discovery.md) | 托盘系统身份发现与一次性隐藏 | Accepted |

早期工程默认值的决策来源见 decision-status（本机历史记录），其中旧待决项不代表当前进度；现状与未解决事项统一见 [当前状态](../product/current-state.md)。停止沿用旧执行计划不撤销本索引的有效 ADR。
