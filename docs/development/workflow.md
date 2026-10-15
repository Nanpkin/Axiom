# 开发与上下文工作流

## 开始任务

读取 AGENTS → [当前交接](../product/current-state.md) → [代码定位表](code-map.md) 的本轮相关行 → 必要代码/测试与 contract。改 UI 读 UI 架构，改变产品/技术边界才查对应范围/架构/ADR；不默认读旧计划。先检查一次 Git 状态、所在分支、工作目录和已有未提交变更，保护其他工作；不要为确认进度重复扫描全库。优先复用当前合适的 checkout，不机械拆出大量 branch/worktree/PR。

复杂任务按 Plan → Implement → Review：写目标/边界/验收和里程碑，再做最小闭环，最后针对实际 diff 验证。已获得授权的范围内继续推进，不为每个常规小步骤反复索要批准；新增架构、扩大范围或不可逆操作另行说明。

## 执行与记录

- Domain/Engine 行为用 fake adapters 和可控时钟验证；真实 Windows 输入与动作在当前场景中尽早接入验证，不再把全部真实能力机械后置到完整底层框架之后。
- 每次修改必须能归因当前目标；发现顺手重构机会写入后续事项，不夹带实施。
- 聊天得出的稳定行为写 architecture/product；架构取舍写 ADR；操作状态写 exec-plan；可执行保证写 tests；数字/环境写性能报告。避免复制同一规范形成多个权威版本。
- 新会话通过当前交接、相关文件和实际 diff 恢复；旧计划已退出执行路线，不要求读完整旧聊天或重审全项目。交付后更新已做、实测边界、未解决和当前任务；易变的分支/工作区状态当场核对，不把历史 PID 或提交当当前运行状态。
- reviewer 对基线到当前提交的 diff 独立审查，报告触发场景、文件位置、影响和验证；不能把未经验证猜测当确定缺陷。

## 验证与提交

只执行当前改动功能及直接受影响边界的相关测试；2026-10-15所有者明确禁止每轮重跑全套，适用于本机和CI。相关验证通过即交付，不为无关失败扩大本轮；文档收尾只检查内容/链接/隐私。SDK固定10.0.401，优先使用系统匹配版本；依赖lock files已提交。构建入口及相关测试示例：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 restore Axiom.slnx --locked-mode
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 build Axiom.slnx -c Release --no-restore
dotnet test tests/Axiom.Engine.Tests/Axiom.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~Axiom.Engine.Tests.BuiltInBrightnessTests.|FullyQualifiedName~Axiom.Engine.Tests.HardwareBrightnessTests.|FullyQualifiedName~Axiom.Engine.Tests.BrightnessAdjustmentTests.|FullyQualifiedName~Axiom.Engine.Tests.BrightnessRestoreTests."
```

NuGet 复用 `%USERPROFILE%\.nuget\packages`，CLI 状态在 `artifacts/tooling/cli`；全新环境需要 NuGet 网络访问。便携/安装打包和安全清理入口见 [分发说明](distribution.md)，临时夹具不重复发布 self-contained runtime。此前沙箱内 MSBuild 曾无诊断失败，获准在沙箱外运行后成功；不要将工具失败误报为通过。性能入口见 [benchmarks](../../benchmarks/README.md)。

行为变更必须配有意义的正反例。涉及后台工作时审查通知源、订阅范围、无工作等待、去重/合并、队列与重试是否有界；不要求每次性能相关改动都测 CPU。明显异常或方案成本难以判断时做针对性诊断；数值测量按 [性能政策](performance.md) 安排，混合负载读数不能作为达标或回归结论。提交前检查 diff、临时文件、敏感数据及文档链接。仅提交当前任务文件；不要 git add 全部后无视既有改动。计划完成才移入 completed 并修复入口链接。

本地提交应为可复核的小步；远程 push/PR 在已配置远程且任务授权时进行，不擅自创建公开仓库。共享历史回滚默认新建 revert 提交；不用 reset --hard/clean 丢弃工作。回滚数据库/系统副作用需独立恢复流程。

默认相关入口为 `pwsh -File tools/test-core.ps1` 或 `bash tools/test-core.sh`，运行前按本轮diff维护 `tools/change-test-suites.txt` 的完整类名/精确方法名。清单是本批范围，不能将上一批亮度清单沿用于无关任务；TRX须证明每个声明项实际有执行，空/未知/全跳过不算成功。文档提交不自动跑功能检查，发布要求准确SHA已成功main检查而不重复功能回归。

## 完成报告

先用日常语言说明新增什么可用功能、如何操作、还有什么不能用，再给必要的验证结果、风险/后续和提交/工作区状态。局部里程碑完成不能描述为整个产品完成，测试数量不能代替用户验收。性能按实际情况写“采用何种低开销策略；数值未测/本次无需测”，不用每轮重复完整实验欠账。没有运行的测试明确写未运行；文档奠基不等于代码或性能完成。

审查聚焦当前改动及直接影响：修复有触发场景和证据的问题，完成相关回归后继续交付。额外轮次需有新缺陷证据、实际变更或未解决的直接风险，不因能继续想象边界情况而无限扩展复审。

## 文档与投入控制

默认阅读顺序以 AGENTS 为准，不扩展成全量检查清单。开始时写用户新增能完成什么以及实际验收路径；纯内部子任务写明消费者。当前状态只更新 current-state，入口/职责变化更新 code-map，稳定交互只更新 workbench-ui，有本轮计划才记录其证据。小修复和文档维护无需为了编号新建计划；文档整理检查内容和链接即可，不启动应用或重跑功能测试。不要把所有历史进度复制进 README/AGENTS/ARCHITECTURE。

UI 变更必须检查真实窗口与核心操作，不以编译成功或测试数量代替体验验证。开发者术语、能力展板和未来规划不放产品主流程。规则启用只表示允许检测，不表示动作正在执行。

旧维护脚本 tools/run-status.ps1 用于服务观察实验，MainWindow/RuleDraftWindow 代码仅供维护；App 默认已打开产品工作台，不能照历史 0005/0006 的路径声称当前主入口仍提供文字草稿。维护实验前先确认对应入口，不恢复成用户默认页面。
