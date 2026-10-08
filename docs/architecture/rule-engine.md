# Rule Engine contract

## 确定性与依赖索引

规则含稳定 RuleId、版本、启用标记、scope、声明的依赖键、纯条件表达式、Intent/Command 模板、priority、时序参数和模式保留策略。禁止任意用户代码、反射脚本、运行时 AI 判断。

规则编译时建立 StateKey/Capability → RuleIds 反向索引。事件只重新计算依赖受影响键的规则；规则编辑更新相关索引。禁止每次事件遍历所有规则。启动/配置重建允许有理由的一次性全量处理并纳入基线。

同一有序输入、配置版本、时钟输入和 provider outcome 必须得到相同决策及解释；时钟与随机源不得藏在纯规则评估中。并行观察可以，但同一资源仲裁按 Engine revision 有序提交。诊断给出 rule id、输入 revision、胜者、抑制/冲突原因。

## Priority / Conflict

先验证授权与 capability availability，再应用 Manual Override 和模式保留策略，然后在有效候选中选最高 priority。多个最高优先级候选目标相同可合并；目标不同时输出 Conflict，停止对该资源自动写入，并暴露冲突。不得用到达先后或随机结果打破平局。低优先级不得覆盖高优先级。显式用户操作先按接管 contract 撤销自动化写入权，不伪装成一个无限大 priority。

相互独立资源可并行执行，但每资源单在途；跨资源事务不属于 V1。Command 默认按条件从非 true → true 的边沿产生一个 commandId；持续 true 不重复命令。恢复/重启时若边沿无法可靠恢复，默认不补发，需用户显式补发策略。

## 时序 contract

| 机制 | 语义 |
|---|---|
| Debounce | 输入保持稳定达到窗口才重新评估；新变化替换同一 pending deadline |
| Minimum Duration | 条件必须连续为 true 指定时长才激活；false/Unknown 取消等待；默认失活即时 |
| Hysteresis | 数值条件使用明确进入/退出双阈值，间隔内保持之前判定；初始 Unknown 不猜状态 |
| Cooldown | Command 两次有效触发间的最短间隔；窗口内触发丢弃并解释，不攒队列；Setting 的最小 apply 间隔另在 Capability 声明 |

顺序：版本/quality 校验 → debounce 稳定输入 → hysteresis 条件 → minimum duration 激活 → 模式/override/priority 仲裁 → command cooldown 或 setting apply 限速 → 执行前复核。Minimum Duration 对外声称的连续 true 必须由原始有效观测保证：false/Unknown/gap 立即打断连续性，不能被 debounce 吞掉；debounce 只延迟重新激活，不延迟已知失效撤销。等待期间必须检查最新数据；deadline 到达不代表旧条件仍有效。安全撤销、权限失效与手动接管立即生效，不受 debounce/cooldown 阻挡。

持续时间用可注入单调时钟；日历规则用墙上时间和显式时区，并在时区/时间/休眠变化后重算。实际 Time Provider 的 DST 重叠/缺失、错过触发是否补发必须在实现前独立定案，不采用每秒扫描。

有界 Event Bus 的状态更新可按 key 合并到最新值，但需要边沿/持续时长的依赖不能随意合并。溢出时标记 gap/Unknown 并恢复快照，不能把快照当成未观察到的完整事件历史。详细规则见 [performance-model](performance-model.md)。
