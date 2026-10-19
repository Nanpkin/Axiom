# 当前交接

更新：2026-10-19。主工作区其他任务未提交文档保持，以下只列当前事实。

## 本轮：启动动作被无关订阅重建取消

- 本机固定入口artifacts/Axiom-preview/Axiom.exe已更新0.2.0-preview.local.20261019.2，干净源码cba7e1b。旧三角色正常退出，统一publish后重启；三角色实际路径/ProductVersion、后台连接、8条规则规范化指纹、托盘配置及登录启动状态/位置核验保持。
- 实机失败记录是3个启动动作已触发，却在发往Agent前取消；60秒等待尚未开始。HDR门禁恢复内层观察时重建共享订阅，原代码释放Host启动源，撤销了无关启动程序/窗口关闭/托盘动作。上轮只改等待未覆盖此故障。
- 现在仅主动重订阅、启动资源不变且无需新基线时保留启动源epoch/序列/值，待新观察就绪再接收工具请求。不重放边沿，不增加重试；真实断线、资源变更、新基线、暂停和停止仍保留取消保护。
- 修复在隔离工作树artifacts/tray-startup-fix、分支codex/startup-observation-cancellation-fix，[PR32](https://github.com/Starvan/Axiom/pull/32)基于PR31。主开发分支codex/ui-refinement已同步并推送99e6b57。未合并main/发新Release，未混入后续视觉重做到本机。

## 验证与实机结论

- 新混合HDR门禁+3类启动命令回归在旧代码稳定失败，修复后3条回执全部成功、无取消；真实断线仍取消且重连不重放。17项直接相关测试通过、零跳过，清单tools/change-test-suites.txt；统一四角色publish通过。
- 本机重启后台后未点击立即隐藏，实际自动完成8/8托盘目标，无此前取消记录；不是完整Windows开机验收。托盘等待仍60秒，上限120秒，原名单/模式/启用及其他任务均未修改。
- 原“首次开机”窗口任务本次资格已经被旧版使用，重启后台按设计跳过；没有重置BootId认领、改frequency或伪造开机。真实下一次开机仍待确认。
- 本机证据仅artifacts/boot-actions-diagnostic.json、tray-startup-*.json及工作树artifacts/test-results/startup-cancel（修复前后）/focused。不上传配置、日志、截图或真实目标路径。技术语义见[任务接入](../development/task-authoring-contracts.md)，原托盘边界见[ADR0010](../adr/0010-tray-icon-discovery.md)。

## 其他保持

- 任务HDR/周时段自动暂停、手动暂停保护、列表事件推送、亮度渐变/恢复及滚轮接管保持；本轮没有改原生窗口/托盘适配器，没有重跑全UI/原生/亮度/性能矩阵。
- 本机共用用户数据，原地更新、不备份/EFS；受控游戏、恢复不明或拒停时保留现场。未开展Windows真实重启、Explorer重建或设备矩阵验收。
