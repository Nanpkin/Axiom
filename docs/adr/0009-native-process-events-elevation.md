# 0009 — 管理员会话的原生进程通知

状态：Accepted。日期：2026-10-04。替代 ADR 0007 的窗口到达提示及不要求管理员权限部分；保留句柄退出等待、身份/路径核对、初始基线和断线语义。

用户明确要求检测无窗口程序、禁止轮询，并明确 Axiom 可以且需以管理员身份启动。普通用户本机订阅 Win32_ProcessStartTrace 返回 Access denied；因此不把窗口事件视为进程创建，也不以 __InstanceCreationEvent WITHIN 定期查询补缺。

Launcher 按当前用户提权启动，Host/Agent/UI 继承令牌；无需安装 Windows Service。登录启动用当前用户交互令牌、最高权限的登录计划任务，不保存密码、不以 SYSTEM 执行动作。退出 UI 不结束后台。权限不扩大任务匹配范围：仍核对当前用户 SID、会话、路径和进程创建时间。

任务直接启动的程序继承 Agent 的管理员令牌，目前没有降权启动选项。Named Pipe 所有者明确指定实际用户 SID；客户端发送前通过 `LocalUserPipe` 核对 User，避免 .NET CurrentUserOnly 使用默认 Owner（提权后可能是管理员组）的差异，不以移除身份验证修复可用性。

Agent 中共享一个按需 Win32_ProcessStartTrace 原生外部事件订阅，WQL 限当前会话，回调只按声明名称路由到有界队列；没有消费者时注销。进程退出等待已有精确句柄，不订阅所有退出事件。只在启动/明确缺口建立快照，无定期枚举、无 WITHIN、无窗口检测降级。订阅停止、解析异常、队列溢出使观察 Unknown，重连重新建立基线，不补发命令。不能保证捕获已在核对前退出的极短进程，也不能保证 WMI 提供者绝不丢通知。

参考：[原生进程创建事件](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/krnlprov/win32-processstarttrace)、[外部事件与轮询区别](https://learn.microsoft.com/en-us/windows/win32/wmisdk/determining-the-type-of-event-to-receive)、[计划任务权限](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)。System.Management 依赖限定 Windows Provider；Domain/Engine 不变。零轮询不等于完整产品 CPU 预算已验收。
