# 开发与验证

日常开发从仓库 [AGENTS.md](../../AGENTS.md) 进入。跨层任务需要跟踪时才建短计划，记录消费者、未决项和验收方式。

## 限定验证

- 功能改动先按diff维护tools/change-test-suites.txt，列完整测试类或精确方法。运行pwsh -File tools/test-core.ps1；Linux入口bash tools/test-core.sh只选平台支持项。
- 修复只复查受影响项，保留其他有效结果；不机械重跑全套/全UI/全原生/媒体。CI范围与本机一致，发布复用准确main检查。
- 构建受影响项目即可；需要交付应用时才用统一打包入口。临时实机夹具优先现有工具/framework-dependent，证据明确区分合成、真实API、UI草稿与真实配置链路。
- 文档轮只检查diff、内容、链接、隐私；不改测试清单、不构建/启停应用、不新发版。性能只按 [政策](performance.md) 的触发条件测量。
- SDK按global.json、依赖按lock files，复用系统SDK/NuGet缓存。工具/环境失败不能记为通过。

## 提交

- 公开仓库排除真实配置、日志、截图、设备标识、本机路径和凭据。改写共享历史须有所有者明确授权。
- 新检出设置[固定提交身份](commit-identity-repair.md)。提交前用 `git var GIT_AUTHOR_IDENT` 和 `git var GIT_COMMITTER_IDENT` 核对两者均为 `Axiom Contributors <63274913+Nanpkin@users.noreply.github.com>`，提交后核对实际记录。

## 文档维护

交接只在版本、实机结果、未决问题或证据入口变化时更新，替换过期条目，目标 15 行以内。文件入口归代码定位表，稳定行为归对应契约，历史从 Git 或专题查。
