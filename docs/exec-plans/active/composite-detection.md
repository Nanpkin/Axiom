# 有界组合检测

用户新增能完成：(程序A或B)且接电且非锁定时保持唤醒或执行允许的一次动作。通用界面独立实现，核心不改WinUI。

协议：AutomationTask.DetectionExpression为leaf/all/any/not不可变树，树使用任务schema3，Detection保留严格等于有序首叶的兼容展示anchor；旧schema1/2不得带树。根深度1、最多深度4/叶16/节点64，all/any至少2子项，not恰好1，叶仅Invocation。Snapshot.CompositeDetectionVersion=1与action.SupportsCompositeDetection共同协商；检测IsStateCondition显式true才可组合，脉冲/启动事件禁止，旧HDR后端不忽略新树。

实现：复用Domain RuleCondition三值All/Any/Not；每叶配置校验、每叶同动作允许对和反馈排除须成立，单backend负责全部条件。SQLite JSON保存树与旧任务，schema3写入数据库版本标记阻止旧应用改写。Host订阅用既有合并Dependencies计划，无轮询。为“非锁定”接入当前会话只读session.lock-state，先通知订阅再读WTS状态，失败Unknown且命令恢复基线。

必要验证：6–8项有意义的树界限/Unknown/旧兼容/路由与反馈/SQLite与严格导入/实际Engine触发和Desired释放，相关Host/Agent编译；不全套或重复目录预算。Windows原生锁定状态只读查询留CI，真实锁屏不在验证操作中。

状态：Compiler/Router/Host、真实嵌套WinUI、SQLite/严格导入、session.lock-state与全部.restore消费者及Host/Agent更新/释放已完成整合；仅持续状态、Unknown取非仍Unknown，离散事件独立。首次保存schema3树事务性升tasks.db至user_version 3，旧版本随后不能继续使用该任务库，新应用继续完整读取旧1/2记录。复合恢复导入/暂停保存重开、树映射、实际流与共享仲裁、真实完整目录原64KiB字段/允许对往返已通过。

交付验收：[PR #14准确检查38359914254](https://github.com/Starvan/Axiom/actions/runs/38359914254)受测头`00272a7f881fbb16845c06c044ec422882d12477`、准确main `dcfd6f1d315c14cd210c28113fd9c763ca5b8066`的[检查38361014974](https://github.com/Starvan/Axiom/actions/runs/38361014974)均全部成功，含Windows原生、自有WTS/Provider去重订阅、合成Host→Agent恢复E2E及完整WinUI构建。真实窗口组移动/焦点/弹窗与WTS/RDP/设备效果仍待实机，不将编译或合成检查记为所有者现场已验。[预览 v0.2.0-preview.20261013.1](https://github.com/Starvan/Axiom/releases/tag/v0.2.0-preview.20261013.1)已实际发布（预发布、非草稿），标签与target_commitish指向该main；发布构建38361440941成功，四项uploaded资产与SHA-256摘要已独立核验，详情见[当前状态](../../product/current-state.md)。
