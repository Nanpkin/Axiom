# 当前交接

更新：2026-10-19（补记10月18日验收）。本机后续更新以当前开发分支交接为准。

## 当前批次：任务级自动暂停

- 分支codex/task-auto-pause，基于已推送的codex/brightness-responsive-fade（PR28）；[PR29](https://github.com/Starvan/Axiom/pull/29)以PR28分支为基底；本批未合并/发Release。
- 用户选择外层HDR/周时段自动暂停，列表直观显示原因；不满足时停止内部检测，手动暂停不被自动恢复。
- RunConditions/schema4原地保存，旧动作TimeWindow仍仅约束执行；旧schema/旧执行方不能忽略门禁。
- Host独立HDR系统通知、时钟/恢复事件与一个最近边界deadline；普通主屏HDR检测共享原生来源。无HDR轮询。
- Engine取消等待/撤销授权，Desired按原保护恢复，恢复读取新基线不重放Command；共享资源由剩余任务继续使用。
- UI自动暂停折叠配置、手动/自动状态及筛选，watch-run-states事件推送，断开提示状态未确认；无列表刷新定时器。
- 移除画面检测内硬编码HDR禁止；用户通过任务配置决定。HDR桌面8位转换估计可能失真，不宣称光度准确，见ADR0011。
- 旧HDR动作仍用独立适配器，声明不支持任务级门禁，UI及导入明确拒绝，不静默丢字段。
- 本批21个唯一相关用例通过；统一publish四角色通过，固定预览更新后3角色来源/版本、UI后台连接、原8任务指纹/托盘/登录启动核对通过。清单tools/change-test-suites.txt，证据见[完成计划](../exec-plans/completed/task-auto-pause.md)。

## 已有本机与亮度边界

- 本批验收固定artifacts/Axiom-preview/Axiom.exe为local.20261018.19，干净源码53628fe。实机HDR初读已知、匹配/不匹配门禁与手动暂停保持通过；实际时间边界列表无需刷新从自动暂停恢复，UI修改HDR条件保存读回通过；未切真实HDR。
- 临时合成任务已删除，原8任务保持；唯一启用画面规则显式RunConditions.Hdr=off、无外层时间限制，Version24，其余字段逐项保持；列表显示主屏HDR关闭时运行。私有证据artifacts/task-auto-pause-smoke及brightness-fade-smoke/autopause-*.json。
- 前批修复反复明暗切换后不恢复：硬件进度检查和精确终点确认分开，设备跳过中间帧时保留已确认实际进度；Unknown/外部变化/取消仍让出，恢复最初基线而非100%。
- 前批7个精确方法、原生6轮与真实画面规则6轮通过；证据artifacts/brightness-fade-native/automatic-repeated-trace.json及brightness-fade-smoke/repeated-restore-live.json，本批不重复。
- 六亮度动作支持渐变1–15秒/请求1–60次每秒，旧缺省10；不是物理帧率保证。工具箱多键组合+滚轮、手动接管保持。当前参数以实时用户配置为准，不回填历史数值。
- 画面GPU稀疏归约、活动最多4Hz、静态阻塞等待、不保存画面；暂停无其他消费者时释放采集。未宣称完整性能达标或手机视觉；软件遮罩暂缓。
- 共用用户数据、原地保存、不备份/EFS。固定预览正常停止→更新→重启；拒停/受控游戏/恢复未知保留现场。
