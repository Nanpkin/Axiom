# Axiom 开发入口

用 `git status -sb` 核对分支和已有改动，在 `codex/` 分支工作。小任务由当前代理直接完成。按用户给的文件、界面文字或能力名在 `src/`、`tests/` 定位；找到实现和对应测试即可动手。入口不明时用 `rg -n '关键词' docs/development/code-map.md` 查匹配行。

## 何时补读

- 接续已有故障、核对预览版本或实机证据时，查[当前交接](docs/product/current-state.md)对应条目。
- UI 改动查[工作台约定](docs/product/workbench-ui.md)相关段落。监测或采集改动查[游戏模式](docs/product/game-mode.md)和[性能政策](docs/development/performance.md)；用户可见的游戏模式变化同步[应用内 Wiki](docs/user/game-mode-wiki.md)并核对入口。
- 跨层、恢复或冲突语义查[架构与契约](ARCHITECTURE.md)。产品范围变化查[功能清单](docs/product/functional-roadmap.md)及[范围](docs/product/v1-scope.md)。所有者手册仅供所有者使用。

## 实现与交付

复用现有声明、Engine 和 UI。UI 经 Host 访问能力，Domain/Engine 独立于 UI、Win32、SQLite 和具体 Provider。区分 Command 与 Desired State，保留 Unknown。程序动作按完整路径；远程和 Web 默认关闭，禁止任意 shell 执行。既有规则不自动加限制，保留两条定时亮度任务。

代码改动按 diff 更新 `tools/change-test-suites.txt`，运行 `tools/test-core.ps1` 或 `tools/test-core.sh` 的定向检查，保留断言与阈值。实机结论须来自对应场景。纯文档检查内容、链接和隐私。

保留已有改动。公开仓库仅提交源码、文档和合成数据。按[提交约定](docs/development/workflow.md#提交)核对身份，按批提交并推送；报告结果和待验项。仅在用户要求或验证需要时更新预览；更新、合并及发布遵循[分发说明](docs/development/distribution.md)。
