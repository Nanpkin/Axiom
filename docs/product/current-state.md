# 当前交接

更新：2026-10-18。按AGENTS控制范围，只查本轮相关代码。

## 当前批次：界面精简与紧凑列表

- 分支codex/ui-refinement，基于53628fe（codex/task-auto-pause），本批未合并或发版。
- 任务列表改为紧凑分隔行，检测→动作摘要；编辑/启停/详情在行尾，搜索和状态筛选保留。自动暂停/未确认原因仍直显。
- 默认与窄窗工具栏适配，编辑器去重嵌套卡片；浅深色背景与工具图标统一。说明采用悬停/点击信息按钮或折叠区，字段提示不再另占整段。
- 活动记录普通详情单行预览并按需查看，异常完整展示；保存、启动及通知记录删除固定冗长尾句。多个条件、程序组等文案去术语。
- UI/Host Release构建零警告；WorkbenchPresentationTests和TaskRunConditionsEditorTests共10项通过。原生默认/窄窗、搜索/筛选、新建取消、说明及工具/设置/活动导航通过；证据仅本地artifacts/ui-refinement-smoke。
- 固定预览尚未更新：正常停止被“游戏正在使用HDR自动恢复”拒绝，保留原进程。独立开发窗口验收后已关闭；原8任务指纹/托盘/登录启动/后台连接保持，仍运行local.20261018.19。

## 当前批次：任务级自动暂停

- 分支codex/task-auto-pause，基于已推送的codex/brightness-responsive-fade（PR28）；本批未合并/发Release。
- 用户选择外层HDR/周时段自动暂停，列表直观显示原因；不满足时停止内部检测，手动暂停不被自动恢复。
- RunConditions/schema4原地保存，旧动作TimeWindow仍仅约束执行；旧schema/旧执行方不能忽略门禁。
- Host独立HDR系统通知、时钟/恢复事件与一个最近边界deadline；普通主屏HDR检测共享原生来源。无HDR轮询。
- Engine取消等待/撤销授权，Desired按原保护恢复，恢复读取新基线不重放Command；共享资源由剩余任务继续使用。
- UI自动暂停折叠配置、手动/自动状态及筛选，watch-run-states事件推送，断开提示状态未确认；无列表刷新定时器。
- 移除画面检测内硬编码HDR禁止；用户通过任务配置决定。HDR桌面8位转换估计可能失真，不宣称光度准确，见ADR0011。
- 旧HDR动作仍用独立适配器，声明不支持任务级门禁，UI及导入明确拒绝，不静默丢字段。
- 正在完成本批精确测试、统一构建与固定预览验收；相关清单tools/change-test-suites.txt，计划docs/exec-plans/active/task-auto-pause.md。

## 已有本机与亮度边界

- 本批更新前固定artifacts/Axiom-preview/Axiom.exe是local.20261018.18，干净源码d4f2328。最新只读预检有8个任务、后台连接与登录启动正常；不能沿用旧10任务数量或覆盖用户新配置。
- 前批修复反复明暗切换后不恢复：硬件进度检查和精确终点确认分开，设备跳过中间帧时保留已确认实际进度；Unknown/外部变化/取消仍让出，恢复最初基线而非100%。
- 前批7个精确方法、原生6轮与真实画面规则6轮通过；证据artifacts/brightness-fade-native/automatic-repeated-trace.json及brightness-fade-smoke/repeated-restore-live.json，本批不重复。
- 六亮度动作支持渐变1–15秒/请求1–60次每秒，旧缺省10；不是物理帧率保证。工具箱多键组合+滚轮、手动接管保持。当前参数以实时用户配置为准，不回填历史数值。
- 画面GPU稀疏归约、活动最多4Hz、静态阻塞等待、不保存画面；暂停无其他消费者时释放采集。未宣称完整性能达标或手机视觉；软件遮罩暂缓。
- 共用用户数据、原地保存、不备份/EFS。固定预览正常停止→更新→重启；拒停/受控游戏/恢复未知保留现场。
