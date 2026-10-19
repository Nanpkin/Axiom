# 构建、本机更新与发布

本页是当前操作入口；历史安装/发布记录见 [旧验收快照](../history/distribution-through-20261016.md)，不作为重跑清单。

## 入口与产物

- 开发依赖：global.json固定SDK，Windows启动器Native AOT需Visual Studio C++构建工具；依赖复用缓存。
- 分发：pwsh -NoProfile -File tools/publish-package.ps1 -Version <版本>。只输出artifacts/release下便携ZIP、安装EXE、BUILD-INFO.json和SHA256SUMS.txt。
- 本机预览：pwsh -NoProfile -File tools/publish-preview.ps1 -Version <本轮明确版本>，固定artifacts/Axiom-preview/Axiom.exe，不建每轮版本夹/备份。
- 一个package-stage和逐项目package-build，运行库在app共享；相同路径依赖须hash一致，不危险裁剪Host/Agent/UI。最终用户无需开发SDK。
- 临时夹具优先framework-dependent。tools/clean-artifacts.ps1清可再生临时产物，保留现预览、最新分发、配置与有效证据；-Deep才清增量bin/obj。构建/清理互斥，先正常结束占用夹具的进程。

## 安全更新本机

功能批次验证后已预授权正常停止→替换→重启，不因Axiom在运行重复询问；纯文档不更新二进制。

1. 核对实际运行来源与活动控制，经应用正常停止流程等待设置恢复和旧UI/后台确实退出。
2. 受控游戏、停止拒绝或恢复不明时保留现场，报告未应用；不强杀绕过保护，也不运行会影响现场的安装/卸载/模拟输入。
3. 用统一publish-preview构建替换，启动后核对路径、ProductVersion/BUILD-INFO、后台连接与原配置保持。
4. 共用原用户数据，不备份/EFS/迁移到打包AppData。原有登录启动保持启用状态并核对指向固定入口；仅必要时用既有更新位置流程。
5. 云端不替换所有者本机。构建成功但未安全替换时明确区分已构建/尚未更新。

## 按有意义功能批次发布

1. codex/分支相关验证后及时commit/push；PR精确head检查与对应包成功，再合入main。
2. 准确main Check source成功后推不可变新标签v<版本>。普通提交、文档收尾不单独发版；同日新功能批次用新末尾序号，不移动旧标签。
3. Publish preview release确认标签属于main并复用该SHA成功检查；实际重新构建四项资产，不拿旧PR/旧版本包发布，不重复功能测试。
4. 核对四文件集合、SHA256、版本、干净源码SHA及ZIP内外来源。仅最终job有contents:write；draft上传核验后转pre-release；仓库当前公开，发布资产也公开。
5. 失败只修/重试失败步骤，版本标签不重写；来源、hash或停止保护失败不能降级为成功。发布工具自身变更才按需运行其相关回归。

## 提交身份与历史来源

新clone先按[固定身份说明](commit-identity-repair.md)设置所有者的GitHub noreply邮箱。2026-10-19经所有者授权修正历史作者，旧源码SHA可按[提交对照表](../history/commit-identity-map-20261019.tsv)找到内容相同的新提交。

此次标签目标随身份修复迁移；既有12个Release资产保留原构建字节、BUILD-INFO和校验值，Release说明同时记录原构建SHA与修复后等价SHA，不伪造重新构建或新SHA的CI通过。历史CI和签名不能直接转移到新SHA；未来功能批次仍按上节验证并使用不可变新标签，不把本次授权当成日常移动旧标签的许可。

## 安装边界

当前用户安装，有开始菜单/可选桌面入口和卸载登记；应用按当前账户最高权限运行，Host尚非注册Windows Service。卸载保留用户数据，只移除所属文件和对应登录任务；安装/升级/卸载不强制结束运行实例。切换位置通过既有更新启动位置处理，尚无自动更新。

包未配置代码签名；干净Windows、不同版本升级、广泛DPI/设备及完整性能不是每轮默认检查。BUILD-INFO只含版本/源码/dirty/平台/时间，不含个人数据；未做的验证明确记录，不能用开发机结果外推。
