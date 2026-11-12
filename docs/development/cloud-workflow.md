# 云端工作边界

默认开发和隐私约束见 [AGENTS](../../AGENTS.md)。云端与本机通过 Git 共享源码，本机运行与验收需在对应环境完成。

- 用 `bash tools/cloud-setup.sh` 设置环境，SDK 由 `global.json` 固定。
- Linux 定向测试用 `bash tools/test-core.sh <清单>`，只选该环境支持的测试。WinUI、Windows 管道和原生 API 用 Windows 环境或 CI。
- Check source 运行声明的测试和 Windows 构建，PR 校验分发包。纯文档由工作流排除。检查按准确 SHA 复用，发布见[分发说明](distribution.md)。

## 历史分支

`git show archive/branches-20261104:branches.tsv` 可按旧分支名查原始提交。恢复开发时从对应提交建任务分支并核对基线。
