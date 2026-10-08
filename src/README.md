# 源码入口

新对话先读 [当前状态与交接](../docs/product/current-state.md)，再按 [代码定位表](../docs/development/code-map.md) 找本轮功能。这里不重复维护进度或历史计划，不要求通读全部源码。

- Domain / Engine：纯状态、规则、仲裁、时间和执行协调；不依赖 UI、Win32、SQLite 或具体 Provider。
- ServiceHost：当前普通用户后台宿主，拥有 Engine、SQLite 和受权 IPC；尚非正式 Windows Service。
- SessionAgent / Providers.Windows：当前会话程序观察、HDR 动作与全局快捷键。
- WinUI：默认 `WorkbenchWindow`，通过 Host 请求；旧 `MainWindow`、`RuleDraftWindow`、`GameHdrWindow` 不是当前产品主入口。
- Launcher：短命 `Axiom.exe` 引导后台和 UI，`--background` 仅启动后台。
- Contracts / Infrastructure：跨进程 DTO、统一数据位置、适配器与存储；当前可执行规则仍由 HDR 兼容后端承载。

职责边界查 [架构地图](../ARCHITECTURE.md) 的相关项；旧计划只在查询历史证据时读取。
