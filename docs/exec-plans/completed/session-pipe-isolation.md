# 会话通道隔离与本机更新

完成：2026-10-26。目标：同用户不同Windows会话各自连接本会话后台，固定预览使用规则库UI且原配置保持。

- 7条端点从仅SID改为SID+实际OS会话编号；保留身份、会话和角色校验，无旧端点回退、命令重试或数据迁移。
- 管理通道最多8处理客户端，保留第9监听实例；修复满连接时Win32 231导致监听任务故障。LocalUserPipeTests 4项通过，容量配置对齐后局部复查通过；规则分类此前3项与实际UI验收复用。
- 旧管理监听故障无法正常退出，先保留现场。所有者随后告知已结束进程，本轮只读确认全部会话旧进程消失；不声称是助手正常停止或已验证旧进程恢复。
- 统一publish-preview成功替换固定artifacts/Axiom-preview，版本0.1.0-local.20261026.3/干净b29e6ec，启动Launcher/Host/Agent/WinUI来源及组件版本、SHA256已核对。
- 新界面显示分类/搜索/状态筛选，后台连接已确认。原9任务规范化指纹与托盘设置保持；当前登录启动已启用且指向固定入口。紧邻更新前启动设置基线因旧跨会话故障为空，未伪称完成该基线比较；当前状态与此前activity-details验收记录相符，未改启动设置。
- 固定预览已应用，无需系统重启。本轮不重复分类全流程、原生动作、性能测试或启动另一套真实会话任务。

证据（仅本机，不上传）：artifacts/session-pipe-smoke/publish-applied.log、lifecycle-verify.json、installed-ui-proof.json、confirmed-fault.txt；测试artifacts/test-results/focused。诊断内存含私有信息，只保存在本机。
