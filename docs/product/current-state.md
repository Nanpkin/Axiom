# 当前交接

更新：2026-11-12。公开仓库，仅内层检出开发/推送；收尾见[AGENTS](../../AGENTS.md)。

## 当前：开机等待托盘图标

- 新版实机开机仍未隐藏。任务正常触发，主程序在触发约25秒后出现，第35秒结束时尚未识别托盘图标。之后蓝牙连接、显示动作确认均有记录，连接前图标已可见，断开恢复可见基线。
- 启动后等待图标最多60秒，启动命令总预算90秒，预留写入/核验时间。等待结束时处理已出现但未收到通知的图标；无周期扫描。11项定向检查通过，包含启动命令/Provider期限一致、取消、晚出现图标和目录预算。整机复测待新版部署。
- 隐藏保留NIS_HIDDEN注册，显示优先清隐藏位；缺失才请求重建。仅有隐藏注册时等待后续可见图标；写后事件加一次短延迟和期限末核验。显式重加或Explorer重启仍可能显示。
- 旧版0.2.0-local.20261109.1（6d5a5de）真实DSX在运行后隐藏/显示/租约恢复验证通过；此验收未覆盖开机延迟。证据仅本机artifacts/tray-state-smoke及test-results。

## 既有能力与依赖

- 分支codex/device-name-tray，[PR52](https://github.com/Nanpkin/Axiom/pull/52)草稿目标codex/optional-condition-restore；前置链52→51→50→48→43未合并，不整理其他对话分支，不发公共Release。
- program.start可选hideTrayIcon默认false；trayExecutable留空沿用启动路径，启动器可指定真实托盘程序。skip仍隐藏，启动失败不隐藏；总预算90秒，启动后等待至多60秒。非空路径严格校验，导入仅已观察别名。
- 蓝牙完整名称忽略大小写、同名分组；已配对经典/LE系统选择器禁用查询，观察AEP IsConnected，独立于HidHide/模拟USB，无周期扫描；失效Unknown须暂停再启用。
- tray.show.restore复用OriginalActionId、通用Desired编译/Host-Agent租约及ConditionalSettingTransaction。固定进程与Shell，允许同进程UID/窗口替换；仅最后可见性仍一致才恢复首次基线。首次已连接可应用，未确认写入不恢复，重建最多5秒。
- 既有本机10条规则中蓝牙仅1条，所有者此前确认连接显示/断开恢复隐藏。启动任务已启用隐藏并明确真实托盘程序；其余配置保持。上轮本机版本0.2.0-local.20261108.4，干净源码78c929c；启动增量19项验证已通过。

## 保留限制

- ServiceHost静默消失根因未修：系统AppHangB1但无线程转储，下次挂起需堆栈；本机证据artifacts/host-hang-smoke。
- PR51恢复开关；PR50硬件亮度首次枚举有限重发现（ADR0011）；PR48工作台/滚轮；PR43启动遮蔽仅直接创建进程（ADR0012）。改目标须重验兼容。
- 最近公共发布v0.2.0-preview.20261028.1。
