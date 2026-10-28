# 当前交接

更新：2026-10-28。分支codex/window-previsibility-research；[PR43](https://github.com/Nanpkin/Axiom/pull/43)草稿，未合并/发Release。外层旧检出禁止推送，开发使用此检出。

## 当前批次：启动遮蔽与手动打开恢复

- 显式program.start窗口选项startup-hidden，最长1–20秒、默认15；旧隐藏模式不迁移。仅x64桌面程序，不用于游戏/受保护/必须管理员程序。
- 目标按同用户/会话非管理员桌面权限创建，挂起初始化保护；应用窗口显示前cloak，覆盖Qt延迟插件。自然隐藏、截止或取消时隐藏后解除；不在结束后继续隐藏。
- 前两轮验收不充分：先漏测普通权限入口，后全部夹具使用-startup而用户任务空参数。真实开机反馈普通权限/无cloak仍打不开；空参数稳定复现Qt逻辑可见与Win32隐藏失配，系统IME误遮蔽另致错误87。
- f6b9344修复：只处理应用目录模块注册的窗口类；结束恢复创建/显示导入，仅保留三项应用激活入口包装，在应用激活被本次隐藏窗口时恢复显示并消费标记。无轮询或持续隐藏；组件可能驻留至目标退出，选项已明示。见[ADR0012](../adr/0012-recoverable-startup-window-guard.md)。
- 空参数实际WindowsProgramControl、独立普通权限调用、同PID打开/关闭/再打开通过；ProgramPluginTests 5项零跳过，原生与统一预览构建通过。

## 当前本机状态

- 固定artifacts/Axiom-preview/Axiom.exe已更新0.2.0-local.20261028.4，干净源码f6b9344；三角色准确路径/版本、原生hash、后台连接核对通过。
- 旧角色经自身界面正常停止并确认退出；本次以更新前9任务为准，任务/托盘指纹保持，登录启动仍正确。未丢弃编辑、改参数、重置开机资格或强杀Axiom。测试结束HECATE已获授权。
- 新版真实执行用户保存的空参数启动任务，活动确认遮蔽完成；启动显示事件cloak=1。等待25秒后普通打开、正常关窗、再次打开通过，原PID保持，两次显示cloak=0。当前HECATE普通权限、窗口可见。
- 原始证据只在本机artifacts/hecate-startup-smoke/actual-task-*、guard-integration-smoke/ordinary-*及preview-update-smoke/*activation*，不提交配置/日志/本机路径。见[实验记录](../development/window-previsibility-research.md)。

## 尚未验收

- 本轮未整机重启，不能把实际任务链复核写成下一次登录开机已通过；未逐帧捕获或验证耳机硬件效果、广泛框架/性能，不宣称任意程序零闪现。
- 外部进程已显示首帧不能事后撤销；独立Desktop未实现。当前公开Release仍为v0.2.0-preview.20261028.1；本批只有开发分支和本机预览。
- 共享用户数据，不备份/迁移；后台停止遵守游戏/恢复保护，不绕过。此前游戏HDR驱动呈现问题不属本次修复。
