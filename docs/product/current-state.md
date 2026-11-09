# 当前交接

更新：2026-11-09。公开仓库，仅内层检出开发/推送；收尾见[AGENTS](../../AGENTS.md)。

## 当前：修正开机隐藏托盘的状态确认

- 本次开机日志确有启动并隐藏成功记录；同一DSX进程随后可见，未发生蓝牙显示动作。确认旧实现把S_FALSE空矩形算作可见，并以NIM_DELETE删除注册，可能过早结束或在程序更新时重现；日志未记录具体重现时刻，不臆断唯一时序。
- 改为NIM_MODIFY/NIF_STATE切换NIS_HIDDEN，保留真实注册；S_OK非空矩形才可见，S_FALSE空矩形另核验注册存在才算隐藏，失败缺失，其余未知报错。仅缺失注册才用TaskbarCreated请求真实重建，不伪造图标。
- 启动隐藏遇到初始隐藏注册会继续等待后来可见图标，期限结束再次核验；确实隐藏才确认成功，无图标仍未确认。写后系统事件加一次100毫秒延迟读和最终期限读，无周期扫描。普通图标更新保留隐藏，显式重加或Explorer重启仍可能显示。
- 复用启动协调和单规则状态回退，配置字段/默认值不变。原进程退出成功空释放，不重绑替代进程。
- 22项相关检查通过、零跳过（托盘20、启动协调1、完整目录预算1，复用有效结果）。真实DSX隐藏→保持→显示→隐藏→租约应用/释放恢复隐藏通过，原进程和UID保持；未完成整机重启复测。证据仅本机artifacts/tray-state-smoke及test-results/{tray-state-accepted,tray-state-order-fixed,focused}。测试改用既有HWND UID约定消除异步登记时序，断言保留；仅清测试专属历史/本轮GUID，不动用户记录、不放宽64条保护。已正常更新本机0.2.0-local.20261109.1（干净源码6d5a5de）；三角色来源/版本、UI/后台连接、原10任务/托盘工具/登录启动指纹保持，当前DSX确认为隐藏。临时夹具二进制已清、保留源码证据。6d5a5de的PR隐私、定向测试及完整构建CI通过，远端包构建中。

## 既有能力与依赖

- 分支codex/device-name-tray，[PR52](https://github.com/Nanpkin/Axiom/pull/52)草稿目标codex/optional-condition-restore；前置链52→51→50→48→43未合并，不整理其他对话分支，不发公共Release。
- program.start可选hideTrayIcon默认false；trayExecutable留空沿用启动路径，启动器可指定真实托盘程序。skip仍隐藏，启动失败不隐藏；总预算35秒，等待至多30秒扣除启动耗时。非空路径严格校验，导入仅已观察别名。
- 蓝牙完整名称忽略大小写、同名分组；已配对经典/LE系统选择器禁用查询，观察AEP IsConnected，独立于HidHide/模拟USB，无周期扫描；失效Unknown须暂停再启用。
- tray.show.restore复用OriginalActionId、通用Desired编译/Host-Agent租约及ConditionalSettingTransaction。固定进程与Shell，允许同进程UID/窗口替换；仅最后可见性仍一致才恢复首次基线。首次已连接可应用，未确认写入不恢复，重建最多5秒。
- 既有本机10条规则中蓝牙仅1条，所有者此前确认连接显示/断开恢复隐藏。启动任务已启用隐藏并明确真实托盘程序；其余配置保持。上轮本机版本0.2.0-local.20261108.4，干净源码78c929c；启动增量19项验证已通过。

## 保留限制

- ServiceHost静默消失根因未修：系统AppHangB1但无线程转储，下次挂起需堆栈；本机证据artifacts/host-hang-smoke。
- PR51恢复开关；PR50硬件亮度首次枚举有限重发现（ADR0011）；PR48工作台/滚轮；PR43启动遮蔽仅直接创建进程（ADR0012）。改目标须重验兼容。
- 最近公共发布v0.2.0-preview.20261028.1。
