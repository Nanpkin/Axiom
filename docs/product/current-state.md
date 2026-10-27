# 当前交接

更新：2026-10-28。功能分支codex/rule-library，[PR39](https://github.com/Nanpkin/Axiom/pull/39)叠于activity-details；未合并/发Release，私有配置/诊断不提交。

GitHub用户名已由Starvan改为Nanpkin（账号ID63274913不变），新旧检出origin与新提交noreply邮箱已同步；通用作者名及旧检出禁止推送保护保持。开发使用本检出，身份设置见[提交身份说明](../development/commit-identity-repair.md)。

## 本机已更新：规则库与会话连接修复

- 固定artifacts/Axiom-preview/Axiom.exe已更新并启动为0.1.0-local.20261026.3/干净b29e6ec；各角色来源、版本、SHA256及真实UI后台连接已核对。原9任务规范化指纹和托盘设置保持，登录启动当前已启用且指向固定入口。
- 所有者结束旧进程后，只读确认所有会话旧进程退出，再经统一publish-preview替换；无需Windows重启。不声称助手正常停止或已验证旧版退出时恢复。紧邻更新前启动设置基线为空，当前读取正常、未改启动设置。
- 7条管道统一追加真实OS会话编号，修复同账户多会话请求串接；保留用户/会话/角色校验，不回退旧端点、不重试命令、不迁移用户数据。
- Automation管理通道保留第9监听实例，处理上限仍8；旧版在诊断中触发Win32 231导致监听故障，本轮已修复。
- LocalUserPipeTests 4项通过，容量配置对齐后局部复查通过；四角色统一发布成功。证据artifacts/session-pipe-smoke及test-results/focused仅本机，[完成计划](../exec-plans/completed/session-pipe-isolation.md)。诊断内存不可上传。

## 规则库UI

- 分类栏+搜索/状态/新建工具栏；普通规则行64逻辑像素，名称编辑，行尾启停，更多菜单含详情/移至分类，暂停原因与未知直显。
- 支持自定义分类创建/改名/删除/逐条归类、数量及搜索状态叠加。ui-rule-collections.json独立原子保存选中分类；失败保留原状态，删除分类不删除规则。
- RuleCollectionsTests 3项及真实分类/搜索/编辑返回/详情/窄窗已通过；固定新版分类/搜索控件再次核对可见，临时验收分类已清除。证据artifacts/rule-library-smoke。
- 活动二级详情沿PR38，旧记录缺失的信息不补造。本轮未重复全UI、原生动作、多会话真实任务或性能验收。

## 保护边界

- ×隐藏UI；停止后台须正常恢复，不强杀。共用原用户数据，不备份/EFS/打包私有副本，拒停/恢复未知留现场。
- 游戏前台HDR故障在全部Axiom/记录器退出后手动开启仍复现；不盲改显示/G-SYNC设置。会话路由修复不等于驱动呈现问题已修复。
- 旧检出禁止推送或引回旧祖先；游戏模式、恢复、采集策略沿原contract/wiki。
