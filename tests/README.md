# 测试入口

按当前改动运行有意义的行为与架构验证。历史测试数不作为当前总数；以本轮实际结果为准。

Domain/Engine 测试覆盖规则、状态、冲突、时序、接管、执行、模式与生命周期；存储/协议测试覆盖已有草稿及 HDR 配置路径。WorkbenchPresentationTests 验证断连、未知和未生效状态不假报成功。纯 UI 文案映射源码链接到测试项目，不引入 WinUI 运行时依赖。

ArchitectureTests 检查引用与编译元数据，UI 仅依赖 Contracts。真实 Windows/游戏/窗口行为须另行实际验证，fake 通过不能代替。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dotnet.ps1 test Axiom.slnx -c Release --no-restore
```

验证政策见 [测试策略](../docs/architecture/testing-strategy.md)；当前交付证据见 [计划索引](../docs/exec-plans/README.md)。性能数值只按 [性能政策](../docs/development/performance.md) 安排。
