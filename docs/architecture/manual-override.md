# Manual Override

Manual Override 表示用户临时或持续接管某个资源的自动化写入权，不是简单调高某条规则的 priority。作用范围为 ResourceKey + user/session scope，必须经过授权的 Engine 操作。

## 状态与生命周期

Override 记录 owner、scope、创建 revision、开始时间、until-release 或显式 expiry、reason。默认用户显式接管持续到显式释放；UI 退出不释放。用户可选择有期限；规则失效/删除本身不撤销接管。持久化与时间异常处理见 [persistence](persistence.md)。

有接管时自动化仍可计算候选与显示解释，但不得写该资源。来自用户的明确操作经授权与 Engine 执行；未开始的自动化 apply 取消，在途且不可取消的动作可能仍生效，必须显示 Pending/实际结果并观测，不能承诺原子接管硬件。

释放或到期后读取最新有效观测和候选再收敛；不要恢复旧快照或执行积压命令。优先级冲突仍适用。模式切换不取消接管；保留其到期/必要监测依赖。

## 外部手动改变

不能把每次 Observed 与 Desired 不同都判为用户操作：可能来自 Provider 延迟、其他程序或设备。外部改变检测是 Capability 特定能力；只有可靠来源或用户明确启用的尊重外部改变策略才创建接管。否则按既定 Desired contract 收敛，同时必须避免振荡并展示原因。具体 Provider 实现前要明确可区分程度及用户策略，不虚构全系统可检测“人类操作”。

## 测试案例

接管期间高 priority 规则也不得写；UI 退出/Engine 重启接管仍有效；到期时旧 Desired 已失效不得恢复；多用户不能接管他人资源；接管与 apply 竞争不产生第二个在途动作；轻量模式保留必要接管保证。
