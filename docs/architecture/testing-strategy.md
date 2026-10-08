# 测试策略

测试验证可观察 contract，不能只镜像内部实现。第一阶段无真实系统副作用，fake clock、fake scheduler、fake Capability executor 注入；不靠 Thread.Sleep 等待时序通过。

| 层级 | 必须覆盖 | 所属阶段 |
|---|---|---|
| Domain | scope/key、typed value、Command/Desired 区分、无效参数 | 首个计划 |
| StateStore/EventBus | 更新去重、revision、epoch 倒退、Unknown、订阅释放、有界队列、gap、取消 | 首个计划 |
| Engine 语义 | 确定性重放、索引只选相关规则、冲突平局、时序边界、Command 边沿、幂等收敛 | 核心规则后续里程碑；首阶段先建立夹具和清晰待办 |
| Override/Mode | 接管竞争、到期、共享依赖、保留低优先规则、切换与在途操作、恢复不补命令 | 模式/收敛里程碑 |
| Architecture | Domain/Engine 不引用 UI、Win32、SQLite、具体 Provider；禁止绕过 Engine | 首个计划起持续覆盖已有项目；宿主建立后扩展 |
| Adapter/IPC | ACL、跨用户、帧限制、重复请求、断连/gap、迁移与磁盘故障 | 对应适配器引入时 |
| Windows 集成 | 真实通知生命周期、会话隔离、休眠恢复、UI 全退、安装卸载 | 真实 Provider/宿主引入时 |
| 性能 | CPU/唤醒、受影响规则数量、风暴背压、回退、模式对照、游戏负载 | 首阶段 harness；完整系统后发布验收 |

## 必须明确的失败行为

规则输入 Unknown 不触发 false 分支；过期 generation 不确认新目标；非幂等命令 timeout 不重发；同 priority 不同目标报冲突；UI 关闭不停止后台；消费者过慢不产生无界内存；关键控制不被风暴饿死；没有有效工作则无主动计时器。

架构测试应检查项目引用、程序集依赖和实际禁止 API（包括 P/Invoke），不能只靠目录名。未来 UI 除 IPC 客户端不可获得 Provider 实例；composition root 的允许引用单列。引入一条临时违规依赖验证测试能捕获后恢复，不把违规代码提交。

测试失败先找根因；产品 contract 错误时先修规范和相关批准记录，不为绿灯删除测试。性能报告必须注明 Engine-only 或 full-system。相关场景的 OS/权限无法获得时明确跳过原因和待验证风险，不将 skip 计为通过。

完成判定：相关构建/测试成功，新增行为有正反例，文档/计划同步，涉及后台工作时有性能策略说明；数值测量按 [性能政策](../development/performance.md) 的阶段安排，不是每轮完成门槛。文档变更只验证文档，无需重跑构建/功能测试。实际 Windows 场景不能由 fake 通过代替，未验证的专项说明其对当前交付的影响，不自动阻塞无关功能。
