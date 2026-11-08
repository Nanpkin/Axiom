# 当前交接

更新：2026-11-08。公开仓库，仅内层检出开发/推送；历史入口见[仓库维护](../development/cloud-workflow.md)，收尾见[AGENTS](../../AGENTS.md)。

## 已交付本机：启动程序增加启动后隐藏托盘

- 用户反馈重启后断开未隐藏；只读日志及进程创建时间证实DSX在租约应用后被手动重启，用户已确认。旧租约固定原进程，未操作新实例；不是蓝牙断开检测失败。现明确原实例退出时成功空释放，未知仍失败，不重绑新进程。
- program.start增加hideTrayIcon，默认false兼容旧任务。启用后ifRunning=skip仍处理托盘，启动失败不隐藏；图标未确认单独报失败，保留已运行程序。事件等待至多30秒，扣除启动耗时，总预算35秒，不周期隐藏。
- 新增可选trayExecutable：留空沿用启动路径，可明确选择启动器真正的托盘程序，不推断子进程。DSX启动器与主程序不同，部署时须为其启动任务启用隐藏并填写已核实主程序路径，其余设置保持。非空严格校验，导入仅接受已观察别名；optional FilePath空值兼容。
- 19项相关检查通过，零跳过：启动声明/编辑/存储/组合/预算/取消及导入14、托盘恢复4、完整目录预算1。新增字段使摘要略超64KiB，仅精简最紧凑说明的重复文字，全部参数/组合/阈值与断言保留。真实DSX已运行→跳过启动→隐藏成功，原进程保持；本机证据artifacts/startup-tray-smoke，测试artifacts/test-results/{focused,startup-tray-recheck,startup-tray-budget}，不上传现场数据。
- 本机已正常退出后统一更新0.2.0-local.20261108.4（干净源码78c929c），三角色来源/版本、后台/UI连接、原10规则/托盘/登录启动保持已核对。已为既有DSX启动任务启用hideTrayIcon并填写真正托盘程序；实际UI核对开关On和路径正确，其他9任务及本任务其他参数逐项指纹保持。尚未做整机重启复测。临时夹具已停止并清二进制，保留源码/本机证据。

## 已交付：蓝牙检测与单规则恢复

- 分支codex/device-name-tray，[PR52](https://github.com/Nanpkin/Axiom/pull/52)草稿目标codex/optional-condition-restore；前置链52→51→50→48→43未合并，不整理其他对话分支。
- 蓝牙完整名称忽略大小写，同名分组；系统已配对经典/LE选择器带IssueInquiry=False，观察AEP IsConnected，不打开HID、不读取模拟USB、无周期扫描。失效Unknown须暂停再启用。
- tray.show.restore以OriginalActionId配对tray.show，复用Desired编译/Host-Agent租约/SerializedRestorableSettingSession/ConditionalSettingTransaction<bool>。固定一个进程及Shell身份，同进程UID/窗口替换仍按可见性判断。
- 已知基线才显示，首次已连接也能应用。条件结束、Unknown、暂停、删除或正常停止，仅最后可见性和原进程仍一致才恢复首次状态；原隐藏才恢复隐藏，原显示保持显示。未确认写入不恢复，重建最多5秒。
- 本机10条中蓝牙仅1条；此前所有者确认连接显示、断开恢复隐藏通过，实际恢复开关往返通过，原9任务/托盘工具/登录启动指纹保持。2600b39的34项本轮检查及PR CI源码/分发包已通过；现场证据artifacts/tray-restore-smoke。
- tray.show以TaskbarCreated请求真实重建；历史UID失配补0–16有限探测。旧全量模板测试的基线计数/白名单落后，本轮只专测改动模板。

## 保留限制

- ServiceHost静默消失根因未修：11/7系统AppHangB1，WER无线程转储；下次挂起需堆栈。旧HDR/快捷键管道满载风险未复现，证据artifacts/host-hang-smoke仅本机。
- PR51恢复开关；PR50硬件亮度首次枚举有限重发现（ADR0011）；PR48工作台/滚轮；PR43启动遮蔽仅直接创建进程（ADR0012）。改目标后重验兼容。
- 最近公共发布v0.2.0-preview.20261028.1；本轮不合并前置链、不发公共Release。
