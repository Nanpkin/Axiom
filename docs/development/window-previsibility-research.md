# 窗口首次显示前干预：最小实验

2026-10-28。首阶段按所有者要求仅研究；后续所有者要求继续开发并授权测试期间结束HECATE。以下原始矩阵保留其阶段边界，后续集成结果见末尾。

## 结论与证据边界

合成Win32 x64程序验证了一个可继续研究的组合：挂起创建进程，在恢复主线程前加载实验DLL，拦截其窗口创建，在进程内WM_CREATE返回阶段设置DWMWA_CLOAK。创建时带WS_VISIBLE的窗口返回时已cloaked；后续ShowWindow、SetWindowPos及跳过WM_WINDOWPOSCHANGING的调用都未清除cloak，消息处理仍响应。

这证明了显示前干预的机制可行，不等于HECATE已实现无闪烁。窗口放在屏幕外，记录窗口状态、DWM属性和消息顺序，未抓取桌面画面、未逐帧验收。没有验证Qt、多窗口/多UI线程、子进程、不同权限、x86、耳机功能或恢复UI交互；未测CPU数值。没有重复SW_HIDE实验。

实验源码及原始JSONL仅保留本机`artifacts/window-previsibility-smoke`，不上传本机证据。主要文件为shared.h、hook.cpp、probe.cpp；原始记录为result.jsonl、external-cloak-result.jsonl、desktop-result.jsonl、suspended-cloak-result.jsonl。夹具不引用产品代码，不接入构建/CI测试清单。所有合成子进程正常退出，未切换输入桌面。

## 实测

| 编号 | 实验 | 结果 | 限制 |
|---|---|---|---|
| 1 | WH_CBT/HCBT_CREATEWND内直接cloak | HRESULT 0x80070006；失败 | 此阶段过早，不能据有HWND就假定DWM已可操作 |
| 2 | CBT创建阶段清除WS_VISIBLE | 仍观察到可见且未cloaked的状态 | 本夹具中的样式修改不能作为抑制保证；后续显示仍可绕过 |
| 3 | CBT安装窗口过程，WM_WINDOWPOSCHANGING清除SWP_SHOWWINDOW | 初始显示、两次ShowWindow和普通SetWindowPos被挡住 | SWP_NOSENDCHANGING绕过；不是完整显示拦截 |
| 4 | 父进程对已创建但尚未显示的子进程窗口设置cloak | HRESULT 0x80070005；子窗口未cloaked | 正常权限复查结果相同；不能把cloak作为通用跨进程隐藏API |
| 5 | CreateDesktop + STARTUPINFO.lpDesktop | 窗口属于独立Desktop，当前Desktop枚举不到；消息处理响应 | 独立Desktop内Shell_NotifyIcon(NIM_ADD)返回FALSE；热键注册成功仅证明注册，不证明输入可达 |
| 5续 | 独立Desktop内销毁窗口后原UI线程重建 | 当前Desktop仍找不到，独立Desktop能枚举到新窗口 | 关闭后重开不会自动改变UI线程Desktop；有窗口时SetThreadDesktop返回170/ERROR_BUSY |
| 6 | 进程内创建隐藏窗口后、显示前cloak | S_OK；后续ShowWindow、SetWindowPos、SWP_NOSENDCHANGING后cloaked均为1 | 设置cloak时必须已在目标进程内；目标主动解除cloak后可重新显示 |
| 7 | CBT安装窗口过程，在WM_CREATE原处理返回后cloak | S_OK；带WS_VISIBLE创建返回时已cloaked；未记录到可见且未cloaked的窗口位置变更 | 该组利用夹具就绪门，不能证明未配合的真实应用能在首窗前安装钩子 |
| 8 | CREATE_SUSPENDED + DLL加载 + 主模块IAT拦截CreateWindowExW/ShowWindow/SetWindowPos | 恢复主线程前3项导入已替换；普通调用及SWP_NOSENDCHANGING被挡住 | GetProcAddress取得ShowWindow再调用可绕过；其他模块/延迟加载/其他API未覆盖 |
| 9 | CREATE_SUSPENDED + 仅拦截CreateWindowExW，在创建调用内安装线程CBT并于WM_CREATE设置cloak | 恢复前1项导入已替换；首窗创建返回已cloaked，普通/动态ShowWindow及SWP_NOSENDCHANGING之后仍cloaked=1；消息响应正常 | 无需夹具就绪门；仍只验证一个合成窗口和主模块导入，不能直接部署到Qt应用 |

补充：对刚以CREATE_SUSPENDED创建、尚未初始化GUI的主线程安装线程WH_CBT返回错误87。待夹具建立GUI队列再安装成功。这排除了“只挂起、外部装一个CBT、恢复即可”的直接路径。编号9在窗口创建API已进入目标线程时安装CBT，绕开该时序问题。

受限实验环境最初无法创建拥有足够访问权的Desktop；按授权在正常权限下只复查编号5并成功。编号4也单独在正常权限下复查，仍拒绝跨进程cloak。原始记录中的未执行字段0和0x8000000a/E_PENDING不是成功；其他模式的热键/托盘字段0表示未测。

## 两种Axiom模式

- **隐藏已由外部启动的程序**：已显示的首帧无法事后撤销。预先观察窗口事件仍不能形成先于显示的同步屏障；跨进程cloak在本机被拒。若在下一次窗口创建前把DLL装入目标GUI线程，或改由Axiom启动，才有机会使用上述进程内方案。不能承诺随时启用规则即可消除已经发生的闪现。
- **Axiom启动并隐藏**：拥有启动前时机。独立Desktop不需要目标配合或修改其代码，但改变了交互环境；挂起后注入并在创建阶段cloak保留原Desktop，实测机制更接近无闪现，但侵入性明显更高。

## 兼容性与后续边界

cloak是DWM呈现遮蔽，不是禁止应用绘制或节省其渲染资源；窗口的WS_VISIBLE仍可能为真，目标主动取消cloak可恢复显示。后续手动打开窗口也不会自动清除cloak，需要明确的解除/恢复策略，不能做成用户无法解释的黑箱。

实验DLL的窗口过程、IAT和加载流程仅用于固定合成夹具：单窗口全局previous窗口过程、主模块静态导入、固定x64和同机系统加载器入口，均不具备生产兼容性。真实方案需要处理每窗口状态、Qt模块导入、动态加载、GUI线程、权限、恢复、错误回退及DLL生命周期；直接挂API还可能被安全软件阻止，影响目标稳定性。不能扩大成全局桌面注入或当成通用任意进程能力。

所有者接受独立Desktop作为备选，且HECATE没有热键、托盘原本已由Axiom隐藏；耳机后台功能仍须专门验证。若采用，必须是明确的启动配置项，说明不会出现在当前桌面、托盘/交互限制及重新打开行为。尚未实现该选项。

Windows任务视图的**虚拟桌面**与CreateDesktop的**独立Desktop对象**不同。前者的公开IVirtualDesktopManager操作已存在的顶层窗口；其API本身未提供CreateProcess启动时指定虚拟桌面的参数。当前实验只验证后者，不把其限制套用到虚拟桌面，也不声称“窗口创建后再移走”能保证首帧不可见。

## 官方依据

- [DWM窗口属性与cloak](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
- [CBTProc创建阶段及返回值](https://learn.microsoft.com/en-us/windows/win32/winmsg/cbtproc)
- [线程/跨进程钩子与DLL要求](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)
- [WM_WINDOWPOSCHANGING](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-windowposchanging) / [SetWindowPos标志](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [Desktop与线程归属](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)
- [虚拟桌面公开接口](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager)

## 后续：恢复交互与实际HECATE

- HECATE包含自身`-startup`入口。正常权限下实测仍有短暂启动窗口；后台运行后再次从原exe入口启动可打开主窗口。原用户实例未退出的一轮测试无效，不作为启动证据。
- 保护扩为每窗口状态、应用目录延迟加载插件、自然隐藏信号与有限截止。Qt的qwindows插件晚于主程序加载，首版漏掉；单靠窗口附加过程也不能覆盖Qt的替换路径。后续在插件加载及CreateWindowEx返回阶段补齐，结束信号核对实际隐藏调用。
- 原生修正版实测：HECATE启动临时窗口显示事件cloaked=1；自然隐藏触发结束；保护后约2.6秒从原入口打开，主窗口cloaked=0、可见，原生失败计数0。
- 实际WindowsProgramControl路径实测：启动显示cloaked=1；第6秒原入口打开cloaked=0；命令完成成功，主窗口可见。取消路径在第1秒解除保护，程序保留，第6秒原入口打开可见、未cloaked。取消后不再抑制后来启动窗口属于预期。
- 证据仅本机`artifacts/guard-integration-smoke`及`window-previsibility-smoke`。不把窗口状态/事件核对称为逐帧验收或耳机硬件功能验收。正式设计边界见[ADR0012](../adr/0012-recoverable-startup-window-guard.md)。

## 用户反馈修正：普通桌面点击

- 前轮夹具的重开与目标均继承管理员权限，验收存在盲点。用户反馈后现场查到目标仍提权、全部窗口cloak=0；普通权限打开无窗口，同权限打开能显示。此前结果不能作为普通桌面点击已通过的证据。
- 启动遮蔽改用经同用户/同会话核对的桌面父进程权限，显式设置正常进程/线程对象权限；保护内部对象不再跨权限按名字打开，而只向本次目标复制私有句柄。不修改UIPI过滤。
- 新回归由管理员夹具建立保护，另一非管理员进程发起普通打开、正常关闭窗口、再次普通打开。两次均可见、cloak=0，目标TokenElevation=0，启动临时窗口仍cloaked=1。原始ordinary-result.json与ordinary-clicks.json仅留本机，不上传。
