# 当前交接

更新：2026-10-18。按AGENTS控制范围和成本；不通读历史。

## 本轮结果与未决

- 固定artifacts/Axiom-preview/Axiom.exe已正常更新/重启为preview.local.20261018.13，干净源码e500c78；三角色路径/版本/UI后台连接、最新10规则、托盘/登录启动均保持。
- 分支codex/brightness-responsive-fade已commit/push，[PR28](https://github.com/Starvan/Axiom/pull/28)合批待CI/合并，未另发Release；旧20261018.2 Release不含本轮修复。实现计划已[完成](../exec-plans/completed/brightness-responsive-fade.md)，无自动启动的后续任务。
- 明亮画面检测支持稳定0秒，活动更新最多4Hz、静态仍DXGI阻塞等待；时段外/HDR/关闭不采集。温和方案0秒稳定/1秒渐变，PDF进入门槛已从70%调60%，其他用户最新配置和备选保持。
- 用户退出明亮画面不恢复的原因已实机复现：连续约55ms Set虽接受，最终读回仍停在自己已发中间值，导致旧逻辑让出。最终硬件写入起点至少100ms，最多1秒连续段，严格段末/终点确认；仅本段accepted序列不倒退的旧值可≤1秒只读等待，未知/外值/倒退/取消/超时停发，无写入重试。
- 最终真实生产DLL100→40→100往返通过，40及100均严格读回，约1.35/1.56秒，资源清理完成；重启后配置哈希保持。只验证本轮行为，不称主观视觉或完整性能达标。私有证据在artifacts/brightness-fade-native，临时二进制清理。
- 初版25、分段7、滞后修正7项相关测试通过；最终节奏仅3个精确方法通过，局部夹具修正只重跑该方法，其余结果复用；见完成记录。CI清单只保留当前直接受影响项。

## 既有边界

- 六亮度动作渐变默认关、1–15秒/默认3秒，恢复固定原目标/最初基线。段内手动值可能被覆盖，pending不能凭旧Confirmed恢复；WMI逐步确认、非渐变不变，详见[契约](../development/task-authoring-contracts.md)。
- 通用规则/分类选择/组合/暂停解释，UI退出后台继续；工具箱托盘管理启动一次或事件持续，时段成功一次与有限失败重试。按[代码定位](../development/code-map.md)查实现，不扩大本轮。
- 画面估算仅SDR主屏GPU稀疏归约，不保存/发送画面，见[ADR0011](../adr/0011-screen-content-brightness.md)。共用用户数据，经Host原地保存，不备份/EFS/私有副本。
- 按[分发说明](../development/distribution.md)正常停止→固定预览→重启；拒停/受控游戏/恢复未知保留现场，不强杀。
