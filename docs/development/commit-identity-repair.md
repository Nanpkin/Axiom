# 提交身份与历史来源

## 固定身份

仓库当前公开。新clone后先在仓库内执行；该设置不包含私人邮箱，也不改全局Git配置：

```powershell
git config --local user.name "Axiom Contributors"
git config --local user.email "63274913+Nanpkin@users.noreply.github.com"
git config --local user.useConfigOnly true
git var GIT_AUTHOR_IDENT
git var GIT_COMMITTER_IDENT
```

新提交、合并、标签均使用这组姓名和邮箱。提交前确认最终身份，提交后用`git log -1 --format=fuller`核对实际结果。命令级`git -c`、`--author`及身份环境变量能覆盖默认配置，因此不能只看`git config user.email`。禁止AI自造GitHub noreply邮箱；发现错误提交须在推送前修正。保留真正的GitHub网页合并提交者记录，不将平台身份伪装为所有者。

2026-10-28所有者GitHub用户名由Starvan改为Nanpkin，账号ID仍为63274913。远端统一使用 https://github.com/Nanpkin/Axiom.git 。旧提交中的带ID noreply邮箱保留，不因本次改名重写历史；参见[GitHub用户名说明](https://docs.github.com/en/account-and-profile/reference/username-reference)。

## 2026-10-19维护记录

所有者明确授权这一次历史作者修复，并选择本地重新clone。此前AI根据项目名编造两组noreply地址，导致GitHub把项目提交关联到其他真实账号；仓库实际推送/合并活动仍来自Starvan。问题发生于提交身份元数据，邮箱关联不授予访问权限。

本次把两组错误noreply邮箱、原匿名项目邮箱和本地项目别名，精确归并到固定身份。改写覆盖全部可写分支和标签；历史代码文件、提交说明、时间与父子关系逐一核对，记录每个旧提交、等价新提交和相同tree。旧提交签名因SHA改变无法保留有效性，已移除，不声称新提交继承原Verified签名。日常仍禁止未经所有者授权改写共享历史。

- [完整提交对照表](../history/commit-identity-map-20261019.tsv)：可写分支/标签历史的255个提交。文档中的旧短SHA可以按前缀查原完整SHA；新SHA对应相同tree。
- GitHub管理的`refs/pull/*`、已关闭PR的旧提交页面、旧活动和旧CI记录无法通过普通分支/标签推送整体重写。服务器可能继续保留旧对象；不能宣称所有旧SHA已从GitHub删除。
- 主页Contributors与Insights使用不同接口；维护后可能异步更新。验收先查新clone的原始作者/提交者和标签，再查GitHub提交关联/统计；长期不一致向GitHub支持申请重算，不重复改写或造空提交碰运气。
- 旧clone、旧工作树和旧聊天上下文须停止向该远端推送。另行保留未提交工作，再重新clone；不要pull旧新历史形成合并，也不要重新上传旧分支或旧标签。
- 本轮只维护Git元数据和文档，不合并5个开放功能PR、不重跑功能测试、不发布新版本。未来功能批次按当前源码重新取得精确CI结果，不能复用旧SHA检查冒充新SHA验证。

## 既有Release来源

保留既有12个Release及48个上传资产的原始字节、BUILD-INFO与摘要；作者维护不重新构建安装包。标签改为内容相同的新源码提交，Release说明记录下面的对照。包内旧源码SHA是原构建事实，不直接改成从未构建过的新SHA。GitHub自动生成的源码归档随标签的新SHA生成，属于相同代码内容的迁移源码。

| Release标签 | 原构建/标签源码SHA | 身份修复后的等价源码SHA |
| --- | --- | --- |
| v0.2.0-preview.20261018.2 | `ecd0daef703a7ef1119ae0b9e298ba3778241a7d` | `a8deacbb1661522e3c4161ff89f072086eb448c7` |
| v0.2.0-preview.20261018.1 | `e3954b47edfef7f5bc613156bc9e0beba63a69cc` | `b1f61827ee7d5bbdc65b18ca331e7aa44e35cd56` |
| v0.2.0-preview.20261017.1 | `38de7b98bbe930a908157a669489b5b8714ed66f` | `de3cc3404ea946aeb869564f6828b6151e98080b` |
| v0.2.0-preview.20261016.2 | `71bd9b3303dd5aa820cffba9bcea51825b7fb296` | `cbdbccb01a91c175fef1d6a7fa913cde5ba898b8` |
| v0.2.0-preview.20261016.1 | `b45ea709e6a5bac7357f14163ffc744e0e45119e` | `aa59109420fb17da1b65141e33765ebcf6e5e232` |
| v0.2.0-preview.20261015.1 | `d54df6d7da977af0c4f228c0dafcb548036985dd` | `9a8a7a361d6c2debe4ff6ffecfb189774d8a416b` |
| v0.2.0-preview.20261014.3 | `f6c3d157c82dd73b465a7568a9c04475393bdc98` | `57bbe104f8617d06509fc858a6d3d8ff9cc29198` |
| v0.2.0-preview.20261014.1 | `43395e364773ea7d2f8ea65404e23d38e916497a` | `f294194eceb46945cf3b12e3e2b5d593792ab3ec` |
| v0.2.0-preview.20261013.1 | `dcfd6f1d315c14cd210c28113fd9c763ca5b8066` | `8c1cc61308b7e7cddd09a00c24e4b283cbc0ca3f` |
| v0.2.0-preview.20261009.4 | `2c5523810232926d2263d4bb3ff26dc8380cafb7` | `c9223dff2681486aab17a0555bcba7e22dbd131f` |
| v0.2.0-preview.20261009.3 | `9e87cbbb044d72411cdacf3bb55cde8937805bf7` | `8597029e3dc118a5e75ac5b0f20c8c02e888230c` |
| v0.2.0-preview.20261009.2 | `e7ee2cbce1324ce02f05b532316340447381b325` | `83ad9a6df95c1ceeafbdb773d3917e43ba1a8091` |

历史CI、构建说明及运行中版本信息仍可引用原构建SHA；通过本表和tree相等证明对应关系。以后的发布继续使用不可变新版本标签，本次迁移不是日常移动旧标签的许可。
