# 条件期间任务栏自动隐藏，结束恢复

用户新增能完成：选择指定程序窗口最大化或其他持续条件，在条件期间请求任务栏自动隐藏，结束后有条件恢复本轮原选项；一次性开启/关闭的原动作仍独立保留。

- 独立`.restore`声明、持续条件组合、暂停最大化模板、严格导入/保存/注册；共用恢复租约负责真实Desired确认与释放，原Command/schema不变。
- 固定本次已核验Explorer/任务栏，真实Known首次AUTOHIDE基线；同租约更新保留首次基线，当前受控位不同或Shell身份未知让出，不操作替代Shell。恢复只改受控位，其他flags保留释放时的当前值。
- 共用5秒单准入、序列化真实原生调用/清理；取消、超时、未确认写入让出，阻塞原生仍保持句柄及closing到真实结束，不重试/重新捕获。
- 四项直接合成恢复case、Agent Windows目标build零警告/零错误。one-shot改用已有ManualTimeProvider在明确写入后推进期限，单case回归通过；无全套/预算/真实任务栏修改。

状态：本adapter、声明/注册/导入/模板/保存及共用Host/Agent真实Desired租约、获胜更新/明确释放均已完成整合；原目标失效或外部改值让出，结束只核验并恢复受控AUTOHIDE位，覆盖整屏检测反馈排除保留。原一次性开/关继续独立，UI退出后台继续。

交付验收：[PR #14准确检查38359914254](https://github.com/Starvan/Axiom/actions/runs/38359914254)受测头`00272a7f881fbb16845c06c044ec422882d12477`、准确main `dcfd6f1d315c14cd210c28113fd9c763ca5b8066`的[检查38361014974](https://github.com/Starvan/Axiom/actions/runs/38361014974)的源码检查均全部成功，含实际Host→Agent租约流的合成恢复E2E；PR另完成便携/安装包构建。合成检查不调用真实任务栏设置；Windows11自动隐藏/多屏/Shell重启/外部改动与WinUI实际交互仍待实机。[预览 v0.2.0-preview.20261013.1](https://github.com/Starvan/Axiom/releases/tag/v0.2.0-preview.20261013.1)已实际发布（预发布、非草稿），标签与target_commitish指向该main；发布构建38361440941成功，四项uploaded资产与SHA-256摘要已独立核验，详情统一见[当前状态](../../product/current-state.md)。
