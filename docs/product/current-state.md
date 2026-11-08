# 当前交接

更新：2026-11-08。公开仓库，开发/推送仅内层检出；历史分支入口见[仓库维护说明](../development/cloud-workflow.md)，收尾见[AGENTS](../../AGENTS.md)。

## 当前：托盘显示接入条件结束恢复，改为单规则

- 所有者要求复用既有恢复开关，将此前DS5连接显示/断开隐藏两条改为一条。分支codex/device-name-tray，[PR52](https://github.com/Nanpkin/Axiom/pull/52)草稿，目标codex/optional-condition-restore；前置链52→51→50→48→43仍未合并，不清理其他对话分支。
- 新增tray.show.restore，OriginalActionId配对tray.show，复用RestorableSettingTaskAction、通用Host/Agent租约、SerializedRestorableSettingSession与ConditionalSettingTransaction<bool>。一个已运行程序路径/实例，固定进程句柄、创建时间、SID、会话、路径及Shell身份；同进程更新图标UID/窗口仍按可见性判断。
- 已知基线才显示，首次已连接也能应用。条件结束、Unknown、暂停、删除或正常停止时，当前可见性仍等于最后确认值才恢复首次可见性；原隐藏才恢复隐藏，原显示保持显示。外部已隐藏不覆盖；进程替代、身份未知或未确认写入不恢复。重建最多5秒，不周期重写。
- 编辑页复用“条件结束后恢复原状态”开关；恢复版只支持单程序，不接受一次性动作自定义等待，不能无损切换时保留草稿说明。两个蓝牙模板改为单个bluetooth-tray-visibility；蓝牙状态允许组合既有条件恢复动作。
- 34项相关测试通过、零跳过（首次夹具线程异常后仅复查18项托盘）；Agent构建通过，真实DSX直接lease显示/释放恢复隐藏已通过。夹具修复消息/清理竞态，后台异常转回测试线程，未放宽断言。证据artifacts/test-results/{focused,tray-restore-recheck}，本机夹具artifacts/tray-restore-smoke，不上传现场数据。
- 尚需统一更新本机、将本次旧两规则替换为单规则，并验证恢复开关/实际连接与断开。当前运行0.2.0-local.20261108.2（1eb175b），原9任务+本次两条共11条。

## 蓝牙检测与前轮实测

- 按完整名称忽略大小写匹配已配对经典蓝牙/LE，系统选择器带IssueInquiry=False，观察AEP IsConnected；不打开HID、不读取模拟USB，无周期扫描。同名设备分组；检测失效为Unknown，须暂停再启用。
- 1eb175b的两规则版本已确认HidHide cloak-on下DSX接管后持续已连接，断开后托盘消失。原9任务、托盘工具、登录启动指纹保持。tray.show定向TaskbarCreated后确认真实图标；DSX历史UID失配补0–16有限探测。
- 旧全量模板测试基线计数/未填字段白名单已落后，本轮专测替换模板，不声称全量方法通过。

## 保留限制与前置链

- ServiceHost静默消失根因未修：11/7 18:03系统AppHangB1，WER无线程转储；下次挂起需堆栈。旧HDR/快捷键管道满载风险尚未复现。证据artifacts/host-hang-smoke仅本机。
- PR51恢复开关7bae1c6；PR50首次硬件亮度枚举有限重发现（ADR0011）；PR48承接44–47和工作台/滚轮；PR43启动遮蔽只保护直接创建进程（ADR0012）。改目标后重验合并结果。
- 最近公共发布v0.2.0-preview.20261028.1；本轮不合并前置链、不发公共Release。
