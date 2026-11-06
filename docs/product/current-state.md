# 当前交接

历史分支恢复入口见[仓库维护说明](../development/cloud-workflow.md)。

更新：2026-11-07。仓库公开。旧检出保留未提交工作，开发和推送在当前检出进行；收尾规则见 [AGENTS](../../AGENTS.md)。

## 当前：条件结束恢复开关

- 分支codex/optional-condition-restore，基于PR50的a254d98；前置链PR50→48→43未合并，不改目标为main或清理前置分支。
- 任务编辑页以OriginalActionId声明配对，为自动/硬件亮度、播放音量/静音、麦克风静音、电源方案、程序优先级、窗口状态/布局、任务栏自动隐藏提供可选恢复开关。复用已有恢复动作与保护，旧规则不迁移；事件/不兼容条件不能开启，不支持的参数拒绝切换且保留草稿。
- 17个相关用例通过、零跳过（组合测试夹具修正后仅重跑该项）；UI构建零警告/错误。统一预览更新与实际UI结果待本轮收尾补充。行为说明见UI文档及应用内游戏模式Wiki；证据仅本机artifacts/optional-restore-smoke。

## 前置：早晨亮度首次失败

- 分支codex/brightness-display-discovery，[PR50](https://github.com/Nanpkin/Axiom/pull/50)草稿，基于PR48当前4adcf97，未合并/发版。11/5初次失败已有具体诊断：物理显示器枚举返回0xC02625E5；下次尝试原生占用。后续成功不推断为自动恢复或忽略用户手动操作。
- 前次仅重读亮度，未覆盖物理发现。本轮仅在首次写入前、此明确错误下释放整批句柄并有限重新发现，重新核对布局/会话/主屏；失败零写入，写后不重放、恢复租约不重新抓基线，契约见ADR0011。
- 相关HardwareBrightness/HardwareBrightnessVcp共17项通过、零跳过；原生Provider及framework-dependent夹具构建零警告。用户随后明确要求直接替换、免本轮实机测试；未执行夹具或冷启动验收。证据artifacts/brightness-discovery-smoke，不上传本机日志/配置。
- 游戏退出、HDR恢复后经应用按钮正常退出，统一更新并重启固定artifacts/Axiom-preview/Axiom.exe为0.2.0-local.20261105.1，干净源码adb0f2a（代码60557cd后仅交接变更）。三角色路径/版本、后台连接、原9任务/托盘指纹及登录启动保持已核对；未重置任务资格。证据lifecycle-verify.json及delivery.json。夹具二进制已清理、源码/证据保留；PR50待前置PR48，保留分支，不整理其他对话工作。

## 待合入的桌面修复

- [PR48](https://github.com/Nanpkin/Axiom/pull/48) 集中承接 #44–47、工作台唤出和滚轮失败恢复，基线为实验性 [PR43](https://github.com/Nanpkin/Axiom/pull/43)。先处理前置功能，改目标后核对与 main 的兼容性及准确提交的 CI。
- 前批 0.2.0-local.20261104.5（d6c3ebb）滚轮修复已随新版保留：末笔读回等待至少100ms，一次手势失败只丢弃本轮输入，恢复监听后可继续反向滚动。该批11个精确场景通过，证据仅在artifacts/wheel-input-recovery；本轮不重复测试。
- 滚轮手势就绪检查识别专用处理器；亮度浮层跟随前台所在屏幕，短暂显示目标百分比和太阳图标。已确认覆盖全屏 Edge，独占全屏和 QQ 未实测。工作台从全屏 ChatGPT 可唤到前台。见 PR48 及本地 artifacts/workbench-recall、brightness-fullscreen、brightness-sun。
- HDR 轮次修复已通过隔离原生检查；整场游戏呈现未完全验收。显示器初次通信仅有限重读；特定旧现场未写入原因仍未确认。详见 #44、#45。
- #43 启动遮蔽仅覆盖直接创建的进程。Steam 另起主程序、真实硬件效果和整机重启仍有限制，详见该 PR 及 [ADR0012](../adr/0012-recoverable-startup-window-guard.md)。
- 最近发布仍为 v0.2.0-preview.20261028.1。仓库整理不启动新发布。
