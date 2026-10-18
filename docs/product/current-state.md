# 当前交接

更新：2026-10-18。本页只保留接手事实；按AGENTS限定范围和成本。

## 当前交付

- 本机入口artifacts/Axiom-preview/Axiom.exe，版本0.2.0-preview.local.20261018.4，干净源码7d1025b。正常停止更新，三角色路径/ProductVersion、后台连接、原8条规则指纹、托盘配置内容及登录启动位置保持已核验。
- 托盘工具设置新增等待图标1–30整数秒，新配置默认30秒；旧配置保留1秒，所有者可改为30秒保存。已有图标立即处理，未出现时等系统通知，无新轮询。[PR25](https://github.com/Starvan/Axiom/pull/25)已推送，尚未合并/发新Release。
- 功能与颜色事件过滤已合入maine3954b4：[PR23](https://github.com/Starvan/Axiom/pull/23)、[PR24](https://github.com/Starvan/Axiom/pull/24)。准确PR/main检查通过，[v0.2.0-preview.20261018.1](https://github.com/Starvan/Axiom/releases/tag/v0.2.0-preview.20261018.1)已发布；同模式亮度/nits元数据不再让出再重入控制。见 [验收记录](../exec-plans/completed/screen-content-brightness.md)。

## 已可用

- 新“显示 / 主屏画面大面积明亮”：估算较亮颜色面积，不只白色；默认进入65%/退出45%、稳定3秒，支持已有普通/硬件亮度恢复动作。自动感知HDR暂停、关闭后通知重建；锁屏/系统关屏/未知释放控制，可选HH:mm时段在时段外真正停采。
- “从常用场景开始…”的夜间亮度模板默认暂停，20:00–08:00、绝对目标40%，须改成低于日常亮度后启用。恢复遵守原目标/基线/外部改动保护，不承诺无条件还原。
- 通用规则、分类能力选择、多对象、组合状态、时间/程序/系统/音频/网络等检测及动作已可用，找实现只查 [代码定位](../development/code-map.md)。UI退出后台继续。
- 工具箱托盘管理提供启动一次/系统通知持续隐藏，关闭保留名单但不恢复图标。时段内一次执行支持持久成功去重和折叠有限失败重试。普通亮度优先内屏，明确无接口才选主屏硬件；详见对应 [完成记录](../exec-plans/completed/README.md)。

## 本轮验证与限制

- 本轮7项TrayToolTests、1项本机原生等待/取消通过；图标延后1.2秒出现后隐藏。WinUI构建零警告错误，真实UI字段可见、空值/小数拒绝、编辑30后取消重开仍为1。详见 [托盘证据](../exec-plans/completed/tray-toolbox.md)。
- UI验收中NumberBox提交时序曾触发两次原配置保存，仅工具版本号合计增加2，名单/模式/启用/等待值未改；已改为直接校验文本，最终复查无保存。没有新增正式规则。
- 画面明亮面积此前GPU/通知/时段UI相关验证通过，详见原验收记录。DXGI最多每秒一次，GPU64×36采样、CPU回读4字节；画面不保存/发送。停采释放资源，非完整后台/GPU/游戏性能达标，见 [ADR0011](../adr/0011-screen-content-brightness.md)。
- 画面检测仅SDR主屏，稀疏估算可能漏细小亮区；真实HDR切换、睡眠/物理关屏、游戏、多GPU及广设备矩阵未测。托盘真实登录启动/Explorer重建和未知身份限制沿用历史证据，本轮未重跑。

## 运行与接续

- 用户数据统一USERPROFILE下.axiom，原地更新、不自动备份/EFS。Host为Service角色，尚非注册Windows Service；所有控制走Host/Engine/Agent，受控游戏/停止拒绝不强杀。
- 下一批 [候选](functional-roadmap.md)未确认不启动；历史只按需追查，不重读全部源码/计划。构建交付按 [分发](../development/distribution.md)。
