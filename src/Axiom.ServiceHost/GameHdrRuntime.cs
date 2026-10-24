using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed class GameHdrRuntime : ICapabilityExecutor, IAuthorizationPolicy, ICommandIdSource, IAutomationRuntimeLifetime, IDisposable
{
    internal AutomationTaskCompiler TaskCompiler { get; } = GameHdrTasks.CreateCompiler();
    private static readonly ValueSchema Bool = new("axiom.bool", 1);
    private readonly EngineHost engine;
    private readonly SqliteGameHdrStore database;
    private readonly Func<GameHdrMessage, CancellationToken, Task<GameHdrMessage>>? requestAgent;
    private readonly string sid;
    private readonly ResourceScope scope;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SourceSession> processSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceSession> displaySources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> sequences = new(StringComparer.Ordinal);
    private readonly HashSet<string> sourceSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<(int, DateTimeOffset)>> instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> baselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> lastApplied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> restoreRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> overriddenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> restoreVersions = new(StringComparer.OrdinalIgnoreCase);
    private GameHdrRuleDto[] rules = [];
    private GameHdrDisplayDto[] displays = [];
    private bool agentConnected;
    private bool ready;
    private bool shutdownRequested;
    private string? watchGeneration;
    private string? agentConnectionId;
    private string state = "等待会话程序";
    private string reason = "请启动后台会话程序。";
    private DateTimeOffset at = DateTimeOffset.UtcNow;

    public GameHdrRuntime(EngineHost engine, SqliteGameHdrStore database, string sid,
        Func<GameHdrMessage, CancellationToken, Task<GameHdrMessage>>? requestAgent = null)
    {
        this.engine = engine; this.database = database; this.sid = sid;
        this.requestAgent = requestAgent;
        scope = ResourceScope.Session(sid, Process.GetCurrentProcess().SessionId);
    }

    private ResourceKey ProcessKey(string name) => new("process.running", name.ToLowerInvariant(), scope);
    private ResourceKey DisplayKey(string path) => new("display.hdr", path, scope);
    private static string RestoreId(string path) => "restore:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..20];

    public async Task InitializeAsync()
    {
        rules = await database.ListAsync();
        engine.ConfigureAutomation(this, this);
        engine.RegisterCapability(new(new("process.running", Bool, Bool, ScopeKind.Session, true, false, false,
            ExecutionLocation.SessionAgent, "same-user-session", true, "available", false, TimeSpan.FromSeconds(8),
            true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation,
            EventFidelity.LatestValue, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true,
            "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean));
        engine.RegisterCapability(new(new("display.hdr", Bool, Bool, ScopeKind.Session, true, false, true,
            ExecutionLocation.SessionAgent, "same-user-session", true, "available", true, TimeSpan.FromSeconds(8),
            true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation,
            EventFidelity.LatestValue, TimeSpan.Zero, TimeSpan.FromSeconds(2), CostClass.Low, true,
            "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean));
        foreach (var rule in rules) RegisterRule(rule);
        AcknowledgePlan();
        engine.StartExecution(new(concurrency: 1, maximumAttempts: 1));
    }

    public AuthorizationDecision Authorize(string principal, ResourceKey resource, CapabilityOperation operation) =>
        new(principal == sid && resource.Scope == scope &&
            (resource.CapabilityId == "process.running" && operation == CapabilityOperation.Observe ||
             resource.CapabilityId == "display.hdr" && operation is CapabilityOperation.Observe or CapabilityOperation.ApplyDesired or CapabilityOperation.Override or CapabilityOperation.ReleaseOverride),
            "same-user-session game HDR capability");

    public Guid NextId() => Guid.NewGuid();
    public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) =>
        ValueTask.FromResult(new Outcome(OutcomeStatus.Rejected, "Game HDR accepts only absolute desired state.", command.CommandId));

    public async ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token)
    {
        var id = Guid.NewGuid();
        try
        {
            if (desired.Key.CapabilityId != "display.hdr" || desired.Key.Scope != scope || desired.Target.Boolean is not { } target)
                return new(OutcomeStatus.Rejected, "Target is not an authorized HDR state.", id);
            var path = desired.Key.ResourceId;
            bool expected;
            await gate.WaitAsync(token);
            try
            {
                if (shutdownRequested || !ready || !agentConnected || !baselines.ContainsKey(path) ||
                    !rules.Any(x => !x.Paused && x.DisplayPath == path) && !restoreRules.Contains(RestoreId(path)))
                    return new(OutcomeStatus.Rejected, "No verified game start or connected observer owns this display.", id);
                expected = lastApplied.TryGetValue(path, out var applied) ? applied : baselines[path];
            }
            finally { gate.Release(); }
            var response = await AgentRequestAsync(new(1, "apply", Guid.NewGuid().ToString("N"),
                DisplayPath: path, Enabled: target, ExpectedEnabled: expected), token);
            if (response.Type != "applied" || response.Error is not null)
            {
                var failure = response.Error ?? "HDR action was not confirmed.";
                await gate.WaitAsync(CancellationToken.None);
                try { SetStatus("切换失败", failure); }
                finally { gate.Release(); }
                return new(OutcomeStatus.Failed, failure, id, FailureKind.Permanent);
            }
            await gate.WaitAsync(token);
            try
            {
                lastApplied[path] = target;
                UpdateDisplays(response.Displays ?? [], false);
                SetStatus("已切换", $"已将 {DisplayName(path)} HDR {(target ? "打开" : "关闭")}，并重新读取确认。 ", forceRecord: true);
                if (restoreRules.Contains(RestoreId(path)) && baselines.GetValueOrDefault(path) == target)
                {
                    engine.RemoveRule(RestoreId(path)); restoreRules.Remove(RestoreId(path));
                    baselines.Remove(path); lastApplied.Remove(path);
                    AcknowledgePlan();
                    SetStatus("已恢复", "游戏退出，已恢复接管前的 HDR 状态。 ", forceRecord: true);
                }
            }
            finally { gate.Release(); }
            return new(OutcomeStatus.Succeeded, response.Reason ?? "HDR state confirmed.", id);
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            await gate.WaitAsync(CancellationToken.None);
            try { SetStatus("切换失败", error.Message); }
            finally { gate.Release(); }
            return new(OutcomeStatus.Failed, error.Message, id, FailureKind.Permanent);
        }
    }

    public async Task<GameHdrStatusDto> StatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!agentConnected) return new(state, reason, at, false, [], rules);
            // A successful action is historical evidence, not the current output mode.
            // Refresh only on demand; never feed this UI read into arbitration or retry HDR.
            try
            {
                var reply = await AgentRequestAsync(new(1, "displays", Guid.NewGuid().ToString("N")), token);
                if (reply.Type != "displays" || reply.Error is not null || reply.Displays is null)
                    throw new IOException(reply.Error ?? "HDR 状态回复无效。");
                var current = reply.Displays;
                var inactive = current.FirstOrDefault(display => display.Enabled == true && display.Active == false &&
                    rules.Any(rule => !rule.Paused && HdrDisplayTargets.Resolve(current, rule.DisplayPath) == display));
                if (inactive is not null)
                    return new("HDR 未生效", $"{inactive.Name} 的 HDR 设置为开启，但当前实际输出不是 HDR；此前切换成功不代表当前仍生效。",
                        DateTimeOffset.UtcNow, true, current, rules);
                foreach (var (path, expected) in lastApplied)
                {
                    var display = HdrDisplayTargets.Resolve(current, path);
                    if (display?.Enabled != expected || display.Active != expected)
                        return new("HDR 状态已变化", "当前 HDR 输出与此前动作确认值不一致；没有自动重复切换，请检查当前显示模式。",
                            DateTimeOffset.UtcNow, true, current, rules);
                }
                return new(state, reason, at, true, current, rules);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                return new("HDR 状态未确认", "当前 HDR 状态读取失败，不能用此前成功记录确认当前已开启：" + error.Message,
                    DateTimeOffset.UtcNow, agentConnected, [], rules);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<GameHdrRuleDto> SaveAsync(GameHdrRuleDto edit, CancellationToken token)
    {
        ValidateRule(edit);
        await gate.WaitAsync(token);
        try
        {
            edit = edit with { DisplayPath = HdrDisplayTargets.Resolve(displays, edit.DisplayPath)?.Path ?? edit.DisplayPath };
            var watched = rules.Where(x => x.Id != edit.Id).Append(edit).Where(x => !x.Paused)
                .SelectMany(ProcessNameSelection.FromRule).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (watched > ProcessNameSelection.MaximumWatched)
                throw new ArgumentException("启用的任务合计最多检测 128 个不同程序。");
            var saved = await database.SaveAsync(edit, token);
            var previous = rules.SingleOrDefault(x => x.Id == saved.Id);
            engine.RecordActivity(new(DateTimeOffset.UtcNow, "规则", previous is null ? "规则已创建" :
                previous.Paused != saved.Paused ? saved.Paused ? "规则已暂停" : "规则已启用" : "规则已更新",
                "配置已保存。" + (saved.Paused ? "该规则已停止检测。" : "后台将按已保存的配置检测。"), ActivityLevel.Success, saved.Title));
            var hadControl = baselines.Count > 0;
            ReleaseRoundControl();
            rules = await database.ListAsync(token);
            if (!saved.Paused && overriddenPaths.Remove(saved.DisplayPath))
                engine.ReleaseOverride(DisplayKey(saved.DisplayPath), sid);
            RegisterRule(saved);
            AcknowledgePlan();
            ready = false;
            instances.Clear();
            foreach (var name in processSources.Keys) UpdateProcess(name, null);
            SetStatus("正在准备", hadControl
                ? "规则已保存；本轮 HDR 控制已让出，现有游戏不再自动恢复，等待下次启动。 "
                : "规则已保存，正在更新进程监控。 ");
            try
            {
                await ConfigureAgentAsync(token);
                if (hadControl) SetStatus("已让出本轮控制", "配置已改变；现有游戏不再自动恢复 HDR，等待下次启动。 ");
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            { agentConnected = false; SetStatus("等待会话程序", "规则已保存；会话程序连接失败：" + error.Message); }
            return saved;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string id, long version, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out _) || version < 1) throw new ArgumentException("规则标识无效。 ");
        await gate.WaitAsync(token);
        try
        {
            var title = rules.SingleOrDefault(x => x.Id == id)?.Title;
            await database.DeleteAsync(id, version, token);
            engine.RecordActivity(new(DateTimeOffset.UtcNow, "规则", "规则已删除", "已删除保存的规则并停止对应自动化。", ActivityLevel.Success, title));
            var hadControl = baselines.Count > 0;
            ReleaseRoundControl();
            engine.RemoveRule(id);
            rules = await database.ListAsync(token);
            ready = false;
            instances.Clear();
            foreach (var name in processSources.Keys) UpdateProcess(name, null);
            SetStatus("规则已删除", hadControl
                ? "已让出本轮 HDR 控制；现有游戏不再自动恢复，等待下次启动。 "
                : "已停止监控删除的规则。 ");
            try
            {
                await ConfigureAgentAsync(token);
                if (hadControl) SetStatus("已让出本轮控制", "规则已删除；现有游戏不再自动恢复 HDR，等待下次启动。 ");
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            { agentConnected = false; SetStatus("等待会话程序", "规则已删除；会话程序连接失败：" + error.Message); }
        }
        finally { gate.Release(); }
    }

    public async Task RefreshAgentAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ready = false;
            instances.Clear();
            foreach (var name in processSources.Keys) UpdateProcess(name, null);
            var reply = await AgentRequestAsync(new(1, "displays", Guid.NewGuid().ToString("N")), token);
            await NormalizeDisplaySelectionsAsync(reply.Displays ?? [], token);
            UpdateDisplays(reply.Displays ?? [], true);
            agentConnected = true;
            await ConfigureAgentAsync(token);
        }
        finally { gate.Release(); }
    }

    public async Task MarkAgentConnectionAsync(string id)
    {
        await gate.WaitAsync();
        try { agentConnectionId = id; }
        finally { gate.Release(); }
    }

    public async Task AgentDisconnectedAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (agentConnectionId != id) return;
            agentConnectionId = null;
            agentConnected = false; ready = false;
            ReleaseRoundControl();
            instances.Clear();
            foreach (var name in processSources.Keys) UpdateProcess(name, null);
            UpdateDisplays([], false);
            SetStatus("会话程序已断开", "检测状态未知，已让出本轮 HDR 控制；等待重新连接与下次游戏启动。 ");
        }
        finally { gate.Release(); }
    }

    public async Task StopAgentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await AgentRequestAsync(new(1, "shutdown", Guid.NewGuid().ToString("N")), timeout.Token);
    }

    public async Task EnsureSafeToStopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (baselines.Count > 0)
                throw new InvalidOperationException("有游戏正在使用 Axiom 的 HDR 自动恢复，请先正常退出游戏，再停止后台。");
            if (!shutdownRequested) engine.RecordActivity(new(DateTimeOffset.UtcNow, "后台", "后台正在停止",
                "已确认可以安全退出，自动化与快捷键监听将停止。"));
            shutdownRequested = true;
        }
        finally { gate.Release(); }
    }

    private async Task ConfigureAgentAsync(CancellationToken token)
    {
        var names = rules.Where(x => !x.Paused).SelectMany(ProcessNameSelection.FromRule)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        watchGeneration = Guid.NewGuid().ToString("N");
        await AgentRequestAsync(new(1, "watch", watchGeneration, ProcessNames: names), token);
        agentConnected = true;
        if (names.Length == 0) SetStatus("已暂停", "没有启用的游戏规则；进程监控已停止。 ");
        else SetStatus("监控中", "正在等待指定游戏启动。 ");
    }

    private async Task NormalizeDisplaySelectionsAsync(GameHdrDisplayDto[] latest, CancellationToken token)
    {
        // Reconnect is a new observation baseline, never an in-game rebinding. Persist the
        // named target once, so later connection-instance changes cannot orphan these rules.
        if (baselines.Count != 0) return;
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            var target = HdrDisplayTargets.Resolve(latest, rule.DisplayPath)?.Path;
            if (target is null || target == rule.DisplayPath) continue;
            var saved = await database.SaveAsync(rule with { DisplayPath = target }, token);
            RegisterRule(saved);
            rules[index] = saved;
        }
    }

    public async Task ProcessEventAsync(GameHdrMessage message, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (shutdownRequested) return;
            if (message.Generation != watchGeneration) return;
            agentConnected = true;
            if (message.Reason == "snapshot-complete")
            {
                UpdateDisplays(message.Displays ?? [], true);
                foreach (var watchedName in rules.Where(x => !x.Paused).SelectMany(ProcessNameSelection.FromRule).Distinct(StringComparer.OrdinalIgnoreCase))
                    UpdateProcess(watchedName, instances.GetValueOrDefault(watchedName)?.Count > 0);
                ready = true;
                AcknowledgePlan();
                if (instances.Values.Any(x => x.Count > 0))
                    SetStatus("等待下次启动", "后台启动前已有游戏运行；这次不切换，以免误判启动前 HDR。 ");
                else SetStatus("监控中", "进程初始状态已核对，等待后续启动或退出。 ");
                return;
            }
            if (message.ProcessName is null || message.ProcessId is null || message.StartedAtUtc is null)
            {
                SetStatus("检测异常", message.Reason ?? "进程状态暂时无法确认；已停止自动切换。 ");
                ready = false;
                ReleaseRoundControl();
                instances.Clear();
                foreach (var watchedName in processSources.Keys) UpdateProcess(watchedName, null);
                if (message.Reason?.Contains("queue overflow", StringComparison.OrdinalIgnoreCase) == true ||
                    message.Reason?.Contains("队列已溢出", StringComparison.Ordinal) == true)
                {
                    try { await ConfigureAgentAsync(token); }
                    catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                    { agentConnected = false; SetStatus("检测中断", "重新获取进程状态失败：" + error.Message); }
                }
                return;
            }
            var name = message.ProcessName.ToLowerInvariant();
            if (!rules.Any(x => !x.Paused && ProcessNameSelection.FromRule(x).Contains(name, StringComparer.OrdinalIgnoreCase))) return;
            if (!instances.TryGetValue(name, out var active)) instances[name] = active = [];
            var instance = (message.ProcessId.Value, message.StartedAtUtc.Value);
            if (message.Running == true) active.Add(instance);
            else active.Remove(instance);
            if (!ready) return;
            engine.RecordActivity(new(DateTimeOffset.UtcNow, "检测", message.Running == true ? "程序已启动" : "程序已退出",
                $"已核实 {name} 的进程身份（PID {message.ProcessId}）。", Subject: name));
            var before = displays;
            var latest = message.Displays ?? [];
            foreach (var path in baselines.Keys.ToArray())
            {
                var current = HdrDisplayTargets.Resolve(latest, path)?.Enabled;
                if (lastApplied.TryGetValue(path, out var expected) && current is { } actual && actual != expected)
                {
                    engine.TakeOverride(DisplayKey(path), sid, "HDR was changed outside Axiom");
                    overriddenPaths.Add(path);
                    baselines.Remove(path); lastApplied.Remove(path);
                    if (restoreRules.Remove(RestoreId(path))) engine.RemoveRule(RestoreId(path));
                    SetStatus("已让出控制", "检测到 HDR 被手动更改，本次游戏退出后不会覆盖你的设置。 ");
                }
            }
            UpdateDisplays(latest, false);
            if (message.Running == true)
            {
                var unavailable = false;
                foreach (var rule in rules.Where(x => !x.Paused && ProcessNameSelection.FromRule(x).Contains(name, StringComparer.OrdinalIgnoreCase)))
                {
                    var path = rule.DisplayPath;
                    if (!baselines.ContainsKey(path))
                    {
                        if (overriddenPaths.Contains(path)) continue;
                        var display = HdrDisplayTargets.Resolve(before, path);
                        if (GameHdrTaskPresentation.UnavailableDisplayReason(display) is { } problem)
                        { unavailable = true; SetStatus("未切换", problem); continue; }
                        var initial = display!.Enabled!.Value;
                        baselines[path] = initial;
                        InstallRestoreRule(path, initial);
                    }
                }
                if (!unavailable) SetStatus("检测到游戏", $"已核实 {name} 的进程身份，正在判断 HDR 目标。 ");
            }
            UpdateProcess(name, active.Count > 0);
            AcknowledgePlan();
            foreach (var path in baselines.Keys.ToArray())
            {
                var selected = rules.Where(x => !x.Paused && x.DisplayPath == path &&
                    ProcessNameSelection.FromRule(x).Any(n => instances.GetValueOrDefault(n)?.Count > 0))
                    .Select(x => x.HdrEnabled).Distinct().ToArray();
                if (selected.Length > 1) SetStatus("规则冲突", "同时运行的游戏要求相反的 HDR 状态，已停止切换。 ");
                else if (selected.Length == 1 && state == "规则冲突")
                    SetStatus("监控中", "冲突已解除，正在按仍运行的游戏规则判断 HDR。 ");
                if (selected.Length == 0 && baselines[path] == HdrDisplayTargets.Resolve(latest, path)?.Enabled)
                {
                    if (restoreRules.Remove(RestoreId(path))) engine.RemoveRule(RestoreId(path));
                    baselines.Remove(path); lastApplied.Remove(path);
                    SetStatus("已恢复", "游戏退出，HDR 保持启动前状态。 ");
                }
            }
        }
        finally { gate.Release(); }
    }

    private void InstallRestoreRule(string path, bool initial)
    {
        var names = rules.Where(x => !x.Paused && x.DisplayPath == path).SelectMany(ProcessNameSelection.FromRule)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var condition = RuleCondition.All(names.Select(x => RuleCondition.Equal(ProcessKey(x), StateValue.From(Bool, false))).ToArray());
        var id = RestoreId(path);
        var version = restoreVersions.GetValueOrDefault(id) + 1;
        restoreVersions[id] = version;
        engine.SetRule(new(id, version, sid, scope, condition, RuleAction.Desired(DisplayKey(path), StateValue.From(Bool, initial)),
            priority: -1));
        restoreRules.Add(id);
    }

    private void ReleaseRoundControl()
    {
        foreach (var id in restoreRules.ToArray()) engine.RemoveRule(id);
        restoreRules.Clear();
        baselines.Clear();
        lastApplied.Clear();
    }

    private void RegisterRule(GameHdrRuleDto rule)
    {
        foreach (var name in ProcessNameSelection.FromRule(rule))
        {
            if (processSources.ContainsKey(name)) continue;
            var key = ProcessKey(name);
            var source = engine.RegisterSource("game:" + name, [key]);
            processSources[name] = source;
            sequences[source.SourceId] = 0;
        }
        if (!displaySources.ContainsKey(rule.DisplayPath))
        {
            var key = DisplayKey(rule.DisplayPath);
            var source = engine.RegisterSource("hdr:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rule.DisplayPath)))[..20], [key]);
            displaySources[rule.DisplayPath] = source;
            sequences[source.SourceId] = 0;
        }
        engine.SetRule(TaskCompiler.Compile(GameHdrTasks.FromLegacy(rule), sid, scope));
    }

    private void UpdateProcess(string name, bool? running)
    {
        if (!processSources.TryGetValue(name, out var source)) return;
        var observation = new Observation(ProcessKey(name), running.HasValue ? StateQuality.Known : StateQuality.Unknown,
            running.HasValue ? StateValue.From(Bool, running.Value) : null, DateTimeOffset.UtcNow);
        PublishSourceObservation(source, observation);
    }

    private void UpdateDisplays(GameHdrDisplayDto[] latest, bool baseline)
    {
        displays = latest;
        foreach (var (path, source) in displaySources)
        {
            var display = HdrDisplayTargets.Resolve(latest, path);
            var known = display?.Supported == true && display.Enabled.HasValue && display.Active == display.Enabled;
            var observation = new Observation(DisplayKey(path), known ? StateQuality.Known : StateQuality.Unknown,
                known ? StateValue.From(Bool, display!.Enabled!.Value) : null, DateTimeOffset.UtcNow);
            PublishSourceObservation(source, observation);
        }
    }

    private void PublishSourceObservation(SourceSession source, Observation observation)
    {
        var sequence = ++sequences[source.SourceId];
        var result = sourceSnapshots.Contains(source.SourceId)
            ? engine.Update(source, sequence, observation)
            : engine.ApplySnapshot(source, sequence, [observation]);
        if (result.Status == UpdateStatus.Accepted && sourceSnapshots.Add(source.SourceId)) return;
        if (result.Status is UpdateStatus.SnapshotRequired or UpdateStatus.Gap)
        {
            result = engine.ApplySnapshot(source, ++sequences[source.SourceId], [observation]);
            if (result.Status == UpdateStatus.Accepted) { sourceSnapshots.Add(source.SourceId); return; }
        }
        if (result.Status is not (UpdateStatus.Accepted or UpdateStatus.Unchanged))
            throw new InvalidOperationException($"Game HDR observation rejected: {source.SourceId}, {result.Status}.");
    }

    private void AcknowledgePlan()
    {
        if (!ready || !agentConnected) return;
        var plan = engine.GetObservationPlan();
        engine.AcknowledgeObservationPlan(plan.Version, plan.References.Keys, guarantees: plan.Requirements);
    }

    private static void ValidateRule(GameHdrRuleDto rule)
    {
        if (!Guid.TryParseExact(rule.Id, "D", out _) || rule.Version < 0 || rule.Title.Length is < 1 or > 120 ||
            rule.DisplayPath.Length is < 1 or > 1024)
            throw new ArgumentException("规则名称、进程名或显示器无效。 ");
        var names = ProcessNameSelection.FromRule(rule);
        if (!string.Equals(rule.ProcessName, names[0], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("程序列表与主进程名不一致。");
    }

    private string DisplayName(string path) => HdrDisplayTargets.Resolve(displays, path)?.Name ?? "显示器";
    private void SetStatus(string newState, string newReason, bool forceRecord = false)
    {
        var changed = state != newState || reason.Trim() != newReason.Trim();
        state = newState; reason = newReason; at = DateTimeOffset.UtcNow;
        if (!changed && !forceRecord) return;
        var level = newState.Contains("失败") || newState.Contains("异常") ? ActivityLevel.Error :
            newState.Contains("冲突") || newState.Contains("断开") || newState.Contains("中断") || newState.Contains("让出") || newState == "未切换"
                ? ActivityLevel.Warning : newState is "已切换" or "已恢复" ? ActivityLevel.Success : ActivityLevel.Information;
        engine.RecordActivity(new(at, "自动化", newState, newReason, level));
    }

    private async Task<GameHdrMessage> AgentRequestAsync(GameHdrMessage request, CancellationToken token)
    {
        if (requestAgent is not null) return await requestAgent(request, token);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var pipe = new NamedPipeClientStream(".", GameHdrProtocol.AgentPipe(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ConnectForUserAsync(2000, timeout.Token);
        await GameHdrProtocol.WriteAsync(pipe, request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(9) }, timeout.Token);
        var reply = await GameHdrProtocol.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException("Agent disconnected.");
        if (reply.Version != 1 || reply.Minor != 0 || reply.Schema != 1 || reply.Scope != "user-session" ||
            reply.RequestId != request.RequestId) throw new InvalidDataException("Agent reply rejected.");
        return reply;
    }

    public void Dispose() { gate.Dispose(); }
}
