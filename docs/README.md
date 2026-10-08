# 文档索引与阅读规则

新对话默认只读 AGENTS 和当前交接，再按本轮功能查代码入口；不通读产品、架构、计划或源码。下面是按需索引，不是必读清单。

| 想知道什么 | 唯一入口 |
|---|---|
| 产品要做成什么、何种功能值得做 | [产品范围](product/v1-scope.md) |
| 现在能用什么、验证到哪里、接手后做什么 | [当前状态与交接](product/current-state.md) |
| 本轮功能在哪些代码和测试里 | [代码定位表](development/code-map.md) |
| UI 怎么组织、怎么表达状态 | [UI 架构](product/workbench-ui.md) |
| 技术边界与对应 contract | [架构地图](../ARCHITECTURE.md) |
| 本轮复杂任务的计划或历史证据 | [执行计划索引](exec-plans/README.md)（旧计划均已退出路线） |
| CPU 策略与何时测量 | [性能政策](development/performance.md) |
| 开发和验证步骤 | [工作流](development/workflow.md) |
| 清理产物、便携/安装包、GitHub Release | [分发说明](development/distribution.md) |
| 私有仓库、云端环境、待确认目标 | [云端工作流](development/cloud-workflow.md) |

## 权威与时效

用户最新明确要求优先；产品文档决定范围和体验，architecture 定义行为边界，ADR 记录已接受取舍，development/*contracts 是对应实现的细化，exec-plan 只记录实施与证据。冲突时指出具体差异再修订，不能拿历史计划改写当前需求，也不能用产品范围悄悄废除可靠性约束。

0001–0015 均已退出当前执行路线；completed 是历史完成证据，archived 是停止沿用但未全项验收的计划，不能混同。foundation-check、performance-baseline 和 decision-status 的早期记录也不是当前待办。其中“下一步”“尚未实现”“先 fake”、旧预设和备份流程只描述当时阶段，不是今天的指令。bootstrap/rule-runtime/runtime-lifecycle contracts 的阶段限制同理；其有效 API 语义仍保留。旧服务状态和文字草稿仅供维护，不自动成为产品功能路线。

## 维护方式

当前事实只在 current-state 更新；代码职责/入口变化只更新 code-map；稳定交互只在 workbench-ui 更新；有本轮计划时写其实施证据，不追写旧计划。交接应明确已实现、实测边界、未解决、当前任务；不能只给历史计划链接让下个对话自行摸排。入口链接过去，不重复粘贴测试数、全部限制和里程碑。保留旧证据；发现过时入口纠正链接或增加时效说明。

所有者手册不是技术规范，按 AGENTS 排除。全量文档审查结论与清单见 0010（本机历史记录），后续任务无需重复全量审查。
