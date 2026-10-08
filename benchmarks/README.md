# Engine-only 性能基线

只测内存更新→依赖选择→有界投递，不评估规则、不产生 Intent、不运行真实 Provider/宿主或模式执行器。[性能预算](../docs/development/performance.md) 定义完整产品指标；此处短基线不宣称 V1 达标。

第一份实际结果见 0001 Engine-only 报告（本机历史记录）。

先按根 README restore/build，然后在仓库根目录、Release、无调试器运行（测量之间串行，避免互相干扰）：

```powershell
pwsh -NoProfile -File tools/dotnet.ps1 run --project benchmarks/Axiom.Engine.Benchmarks -c Release --no-build --no-restore -- --filter '*' --artifacts artifacts/performance/bootstrap-20260926/bdn
pwsh -NoProfile -File tools/dotnet.ps1 run --project benchmarks/Axiom.Engine.Benchmarks -c Release --no-build --no-restore -- harness matrix artifacts/performance/bootstrap-20260926 3
pwsh -NoProfile -File tools/dotnet.ps1 run --project benchmarks/Axiom.Engine.Benchmarks -c Release --no-build --no-restore -- harness idle artifacts/performance/bootstrap-20260926 15
pwsh -NoProfile -File tools/dotnet.ps1 run --project benchmarks/Axiom.Engine.Benchmarks -c Release --no-build --no-restore -- harness setup artifacts/performance/bootstrap-20260926
```

- BDN 0.15.8 ShortRun：100/1,000/10,000 条依赖声明，各场景仅 10 条相关；更新+选择、索引、更新+选择+同步出队三个方法。每次事件创建值的成本不计入微基准（不可变输入预建）。
- matrix：每参数预热 10,000 次，100/1,000 events/s，有限 10 ms 批次（每批 1/10 事件），输出实际速率和处理 p95/p99 µs；CPU 包含生成器的有限计时器。之后 100,000 次风暴不消费，验证有界脱离和 gap。脱离后事件不是已投递，丢弃计数仅指终止当时队列和溢出项。
- idle：100 条依赖、预热更新 10,000 次并等待 2 秒，三个独立 15 秒测量窗口；每窗口只有一个 harness 终点计时器，Engine 使用可控 ports，不调度任何 deadline。记录总进程 CPU、分配/GC、Private Bytes；CPU 计量粒度可能给出 0，不能推出全系统无唤醒。
- setup：每规模先预热，再测 3 次 Engine/来源及完整依赖索引建立的时间与当前线程分配；把有理由的一次性全量配置处理也纳入基线。
- JSON/CSV/log 等原始产物位于忽略的 `artifacts/performance/<run-id>`。新一次实验换目录以保留结果；第三参数可调整 matrix/idle 的有限时长（1–3,600 秒）。

真实调度适配器、OS 唤醒归因、I/O、1 秒 CPU 窗口分位数、长时稳定性、模式/游戏与完整后台结果在后续阶段测量。
