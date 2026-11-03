# 当前交接

历史分支恢复入口见[仓库维护说明](../development/cloud-workflow.md)。

更新：2026-11-04。仓库公开。旧检出保留未提交工作，开发和推送在当前检出进行；收尾规则见 [AGENTS](../../AGENTS.md)。

## 待合入的桌面修复

- [PR48](https://github.com/Nanpkin/Axiom/pull/48) 集中承接 #44–47、工作台唤出和滚轮失败恢复，基线为实验性 [PR43](https://github.com/Nanpkin/Axiom/pull/43)。先处理前置功能，改目标后核对与 main 的兼容性及准确提交的 CI。
- 本机运行 0.2.0-local.20261104.5（d6c3ebb）。末笔读回等待至少100ms，一次手势失败只丢弃本轮输入，恢复监听后可继续反向滚动。该修复 11 个精确场景通过，后台连接、9 个任务、托盘及登录启动已核对。证据仅在 artifacts/wheel-input-recovery。
- 滚轮手势就绪检查识别专用处理器；亮度浮层跟随前台所在屏幕，短暂显示目标百分比和太阳图标。已确认覆盖全屏 Edge，独占全屏和 QQ 未实测。工作台从全屏 ChatGPT 可唤到前台。见 PR48 及本地 artifacts/workbench-recall、brightness-fullscreen、brightness-sun。
- HDR 轮次修复已通过隔离原生检查；整场游戏呈现未完全验收。显示器初次通信仅有限重读；特定旧现场未写入原因仍未确认。详见 #44、#45。
- #43 启动遮蔽仅覆盖直接创建的进程。Steam 另起主程序、真实硬件效果和整机重启仍有限制，详见该 PR 及 [ADR0012](../adr/0012-recoverable-startup-window-guard.md)。
- 最近发布仍为 v0.2.0-preview.20261028.1。仓库整理不启动新发布。
