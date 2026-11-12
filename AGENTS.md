# Axiom 开发入口

用户最新要求优先。Axiom 是 Windows 本地自动化工具，规则确定执行，结果可解释、可暂停，关闭 UI 后后台继续工作。

## 默认小修路径

1. 核对分支和工作区，保留已有改动。阅读[当前交接](docs/product/current-state.md)，用[代码定位表](docs/development/code-map.md)找相关入口。其余文档按改动查对应段落。
2. 从现有入口复现问题或获取一条能定位的证据，修最小范围。小修由当前代理直接完成，复杂且可独立验证的工作才分工或写计划。
3. 跑能区分修复前后的定向检查。代码改动按本轮 diff 更新 `tools/change-test-suites.txt`，用 `tools/test-core.ps1` 或 `tools/test-core.sh`；清单无效就修清单。失败先核对准确断言和证据，只复查受影响项。实机验收用真实故障场景，不能用替代场景宣称通过。
4. 一批相关改动一次提交并推送；交接有新事实就随同提交。只有用户要求或验证必须在本机运行时才更新预览。纯文档检查内容、链接和隐私。汇报结果、关键验证和未验证项，技术细节保留在代码或对应契约。

## 设计与验证约束

- 复用声明、Engine 和现有 UI。UI 经 Host 访问能力；Domain/Engine 独立于 UI、Win32、SQLite 和具体 Provider。程序动作按完整路径。远程和 Web 默认关闭。禁止任意 PowerShell/CMD 执行能力。
- 分清 Command 与 Desired State；Unknown 保持未知；恢复、冲突和手动接管遵守[契约与 ADR](ARCHITECTURE.md)。监测优先系统事件、共享按需订阅，轮询例外和性能验收见[性能政策](docs/development/performance.md)。保留测试断言和阈值。
- 监测或采集改动先查[游戏模式](docs/product/game-mode.md)相关段落。游戏模式的用户可见行为变化，同步更新[应用内 Wiki](docs/user/game-mode-wiki.md)并核对入口。既有规则不自动加限制，保留两条定时亮度任务。
- UI 变更查[工作台约定](docs/product/workbench-ui.md)，范围变化查[功能清单](docs/product/functional-roadmap.md)和[产品范围](docs/product/v1-scope.md)。所有者手册仅供所有者使用。

## 交付与安全

- 使用 `codex/` 分支。公开仓库仅提交源码和合成数据，保护配置、日志、截图、设备标识、本机路径和凭据。保留其他任务的改动，改写共享历史须有所有者明确授权。
- 新检出设置[固定提交身份](docs/development/commit-identity-repair.md)。提交前用 `git var` 核对作者和提交者均为 `Axiom Contributors <63274913+Nanpkin@users.noreply.github.com>`，提交后核对实际记录。
- 本机预览已有正常更新授权。按[分发说明](docs/development/distribution.md)核对来源与控制状态、正常退出并确认恢复、更新固定预览、重启核对版本和连接。受控游戏、停止拒绝或恢复不明时保留现场。开发和正式启动共用原数据，配置原地更新，不自动备份或启用 EFS。
- [分发说明](docs/development/distribution.md)是合并和发布流程的唯一入口。复用准确 SHA 的检查；本轮分支确认已入 main 且无 PR 或堆叠依赖引用后再清理，改基线后只复验受影响接口。清理仅限本轮临时产物，保留运行预览和有效证据。
- [当前交接](docs/product/current-state.md)只留当前版本、实机结果、未决问题和证据入口，目标 15 行以内。历史从 Git 或对应专题查。
