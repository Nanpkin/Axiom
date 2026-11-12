# 当前交接

更新：2026-11-12。

- 本机预览 `0.2.0-local.20261112.2`（`5b85f7f`）。所有者确认整机重启后 DSX 托盘隐藏；另一次实测确认 DS5 连接显示、断开恢复隐藏。
- 启动隐藏与普通隐藏共用实现，按配置的完整路径识别。启动时等待注册稳定再隐藏；DSX 是实测样本。运行细节见[工作台说明](workbench-ui.md)，本机证据在 `artifacts/tray-state-smoke`。
- [PR 52](https://github.com/Nanpkin/Axiom/pull/52) 为草稿，依赖 51 → 50 → 48 → 43。修复源码的 5 项本机测试通过，远端连续隐藏用例等第二次通知超时。未合并或发公共包。
- ServiceHost 静默退出仍待定位。已有 AppHangB1，复现时需要线程堆栈；本机证据在 `artifacts/host-hang-smoke`。
- 最近公共发布 `v0.2.0-preview.20261028.1`。
