# 规则草稿持久化开发预览 contract（0006）

> 时效说明：本文记录对应阶段的实现/证据；“尚未实现”“下一步”和入口步骤指当时范围。当前产品以 [当前状态](../product/current-state.md) 为准，维护该实现时仍遵守本文有效 contract。

本阶段仅保存**不可执行的规则意图草稿**。`EventLog` 状态仍只供显示；草稿不进入 `SetRule`、规则索引、Capability 或执行器。WinUI 始终标明“草稿 · 不可执行”。未来转换为可执行规则必须另行确定结构化条件、真实 Provider、动作授权和迁移。

## 数据和写入

- 每条草稿包含 GUID、从 1 开始的单调版本、名称（1–120 字符）、条件与动作说明（各 1–2000 字符）、UTC 更新时间。当前上限 64 条；没有删除、启用或运行操作。
- UI 只依赖 `Axiom.Contracts`；独立开发宿主把 `IRuleDraftStore` 接入 Engine。Engine 校验拥有者 SID、字段和版本；`Axiom.Infrastructure` 的 `SqliteRuleDraftStore` 是唯一数据库写入者。UI 不打开 SQLite。
- 新建携带 expected version 0，编辑携带读取到的版本。SQLite 事务内读取版本并做 CAS；不匹配返回 `conflict` 和当前版本，不覆盖已提交值。`saved` 仅在 `Commit()` 返回后生成。写入、锁等待、磁盘或完整性失败不返回 `saved`；UI 保留输入并显示失败。
- 路径为当前用户的 `%USERPROFILE%\.axiom\preview-rule-drafts\rules.db`，0015 改用不受打包父应用 AppData 重定向影响的数据根。宿主启动时把该目录和数据库文件 ACL 限制为当前用户 SID 与 SYSTEM，拒绝数据库目录/文件的重解析点；没有正式 Service account 或跨用户数据库共享。
- 当前 schema `user_version=2`，表 `rule_drafts(id, version, title, condition_text, action_text, updated_at_utc)`。新库在事务中创建。已知 v1 在原库事务性添加 `updated_at_utc`；按 2026-10-01 用户要求不再生成迁移备份。未知较新版本、已有但无版本的数据库、完整性检查失败均拒绝启动；不能用旧二进制直接读取 v2，没有显式备份时不承诺旧版回退。
- 当前配置写入使用 SQLite rollback journal、`synchronous=FULL`、2 秒 busy timeout 和单宿主串行连接。未测断电耐久或 CPU/唤醒指标；这些参数只适用于低频草稿编辑预览。

## Pipe 与权限

- 独立规则 Pipe `axiom-rule-drafts-v1-<当前用户 SID 中横线改下划线>`；一连接一请求，JSON 小端 4 字节长度帧，最大 65,536 字节；最多 8 个在途连接，单连接 30 秒截止。协议 major/minor/schema 均为 `1/0/1`，scope 固定 `user`，请求含 ID 和不超过 30 秒的 deadline。
- Pipe DACL 仅允许当前用户与 SYSTEM，并设置远程客户端拒绝标志；宿主每个请求用 Pipe 客户端 OS token 重新核对 SID。请求 payload 不含可信身份字段，未知字段拒绝。只允许 `list`、`get`、`save`；不存在脚本、运行或服务控制操作。
- `get` 只返回完整草稿；`list` 返回摘要。冲突与错误是显式响应。连接/回复丢失后的结果可能未知：客户端须重新读取 ID/版本，不自动用新 ID 重放。当前 request ID 只做响应关联，没有跨重启去重账本；草稿 CAS 限制重放覆盖。

## 用户流程与边界

双击仓库根目录 `Start Axiom Status.cmd`，在状态窗口点“管理规则草稿”，新建或选取已有草稿，填写名称、条件说明和预期动作说明后保存。只有收到 `saved` 才显示“已保存”；冲突提示重新读取，失败保持输入。关闭 UI 不停止开发宿主；重新打开后从宿主 SQLite 读取。

本预览不是正式 Windows Service/Session Agent、机器级授权设计或可执行规则编辑器。未验证其他 Windows 用户或远程机器的实际连接拒绝、真实断电/损坏恢复、正式服务安装和性能预算；相关发布门槛仍开放。
