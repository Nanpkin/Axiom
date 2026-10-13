# 有界组合检测

用户新增能完成：(程序A或B)且接电且非锁定时保持唤醒或执行允许的一次动作。通用界面独立实现，核心不改WinUI。

协议：AutomationTask.DetectionExpression为leaf/all/any/not不可变树，树使用任务schema3，Detection保留严格等于有序首叶的兼容展示anchor；旧schema1/2不得带树。根深度1、最多深度4/叶16/节点64，all/any至少2子项，not恰好1，叶仅Invocation。Snapshot.CompositeDetectionVersion=1与action.SupportsCompositeDetection共同协商；检测IsStateCondition显式true才可组合，脉冲/启动事件禁止，旧HDR后端不忽略新树。

实现：复用Domain RuleCondition三值All/Any/Not；每叶配置校验、每叶同动作允许对和反馈排除须成立，单backend负责全部条件。SQLite JSON保存树与旧任务，schema3写入数据库版本标记阻止旧应用改写。Host订阅用既有合并Dependencies计划，无轮询。为“非锁定”接入当前会话只读session.lock-state，先通知订阅再读WTS状态，失败Unknown且命令恢复基线。

必要验证：6–8项有意义的树界限/Unknown/旧兼容/路由与反馈/SQLite与严格导入/实际Engine触发和Desired释放，相关Host/Agent编译；不全套或重复目录预算。Windows原生锁定状态只读查询留CI，真实锁屏不在验证操作中。

进度：协议与共享数据模型已确定；核心编译、保存、后台和状态叶实施中。由负责人整合UI与明确.restore动作、执行Git发布。
