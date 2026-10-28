# 安全边界

本地优先不意味着所有本机客户端都可信。威胁包括：普通进程冒充 UI/Agent、跨会话操作、畸形 IPC、规则文件被修改、日志泄露、未来远程客户端越权。

## 权限原则

- 用户于 2026-10-04 明确要求管理员启动。当前预览 Launcher 在同一账户内通过 UAC 提权，Host/Agent/UI 继承管理员令牌；登录后台用当前用户最高权限交互任务，不保存密码、不切换 SYSTEM。跨账户提权被拒绝；数据仍是同一用户目录。见 [ADR 0009](../adr/0009-native-process-events-elevation.md)。权限不会取消能力/资源/身份边界。
- 每个 Capability 声明权限与资源白名单；Engine 校验请求者和 scope，执行端再校验当前实际权限。
- program.start的显式实验启动遮蔽按同用户/同会话的非管理员桌面权限创建目标；父进程属性、环境及进程/线程对象权限均核对，不能把后台管理员权限带给目标而阻止普通入口唤醒。内部句柄仅复制给本次目标，拒绝身份不明/提权桌面，不放宽窗口消息过滤。见[ADR0012](../adr/0012-recoverable-startup-window-guard.md)。
- Service 不接受任意 PowerShell/CMD、任意可执行路径或自由形式系统调用；不提供“万能执行器”作为调试后门。
- Named Pipes 验证 OS 身份并设严格 ACL；数据库、配置、可执行文件与更新路径设置相应文件权限。
- 日志减少用户路径、进程参数等敏感数据，按需脱敏；默认不采集按键内容、屏幕或窗口文本。

## 必须验证的故障

越权请求必须在副作用前拒绝；无法验证身份/权限则 fail closed。会话注销、用户切换、权限撤销立即取消未执行请求；已在途动作不可取消时明确记录结果并重新观测，不声称瞬时回滚。

规则导入必须 schema/范围验证，不执行脚本或反序列化任意类型。规则配置仍需拥有者授权；已批准某个 Provider 不等于批准其所有操作。Manual Override 也是受权操作，不能跨用户接管。

## 发布前门槛

完成服务身份、管道 ACL、数据目录权限、安装更新/卸载、签名与敏感日志审查；做普通用户越权和多会话隔离测试。具体机制见 待决事项（本机历史记录），不得把“未定”解释为允许忽略。

未来远程入口遵守 [remote-control-boundary](remote-control-boundary.md)，默认不启动；AI 不在鉴权、冲突、状态判断或执行路径中。

0004 开发预览只支持同一当前用户的固定服务只读 Pipe；明确 ACL、远程拒绝标志及本机身份复核见 [显示 contract](../development/service-status-display-contracts.md)。正式 Service account、多用户和安装安全门槛未因此关闭。

0006 的规则草稿 Pipe 与 SQLite 数据目录/文件同样限制为当前用户 SID 和 SYSTEM；每个请求核对 OS token，伪造身份字段和未知操作会被拒绝。草稿只能存文字，不能执行。实际跨用户和远程机器拒绝测试、正式 Service account 与安装安全门槛仍未完成，见 [草稿 contract](../development/rule-draft-persistence-contracts.md)。

0008 的游戏 HDR 管道在当前用户会话内除 OS SID 外还核对连接进程 PID/会话及预期宿主/Agent 可执行文件名，Agent 只接受宿主发出的有限 `watch/displays/apply/shutdown` 操作；两条服务器管道拒绝远程客户端。当前开发预览不将文件名核对等同于代码签名或正式跨会话信任，发布前门槛继续有效，细节见 [游戏 HDR contract](../development/game-hdr-preview-contracts.md)。
