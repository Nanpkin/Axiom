using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

// Description/compilation is pure. Resource observation and execution stay in existing ports.
public interface ITaskDetectionCompiler
{
    TaskCapability Descriptor { get; }
    RuleCondition Compile(TaskInvocation configuration, ResourceScope scope);
    RuleCondition Compile(TaskInvocation configuration, ResourceScope scope, string taskId) => Compile(configuration, scope);
}
public interface ITaskActionCompiler
{
    bool SupportsTimeWindow => false;
    TaskCapability Descriptor { get; }
    RuleAction Compile(TaskInvocation configuration, ResourceScope scope);
    RuleTiming Timing(TaskInvocation configuration) => new();
}

// Trusted, explicitly registered modules. Catalog discovery is side-effect free;
// common storage, Engine scheduling and session transport are supplied by the host.
public interface IAutomationPlugin
{
    string Id { get; }
    IEnumerable<ITaskDetectionCompiler> Detections { get; }
    IEnumerable<ITaskActionCompiler> Actions { get; }
    ImmutableArray<TaskCombination> Combinations { get; }
    IEnumerable<CapabilityBinding> Bindings { get; }
}

// A runtime adapter owns persistence and lifecycle for its registered combinations.
// Implementations must commit before confirming Save and preserve optimistic versions.
public interface IAutomationTaskBackend
{
    ImmutableArray<TaskCombination> Combinations { get; }
    Task<AutomationSnapshot> ReadAsync(CancellationToken token);
    Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token);
    Task DeleteAsync(string id, long expectedVersion, CancellationToken token);
}

public interface IAutomationRuntimeLifetime
{
    Task EnsureSafeToStopAsync(CancellationToken token);
}

public sealed class AutomationTaskCompiler
{
    private readonly Dictionary<string, ITaskDetectionCompiler> detections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITaskActionCompiler> actions = new(StringComparer.Ordinal);
    public void Register(ITaskDetectionCompiler compiler)
    {
        CheckDescriptor(compiler.Descriptor, TaskCapabilityKind.Detection);
        if (!detections.TryAdd(compiler.Descriptor.Id, compiler)) throw new ArgumentException("检测类型重复。");
    }
    public void Register(ITaskActionCompiler compiler)
    {
        CheckDescriptor(compiler.Descriptor, TaskCapabilityKind.Action);
        if (!actions.TryAdd(compiler.Descriptor.Id, compiler)) throw new ArgumentException("动作类型重复。");
    }
    public ImmutableArray<TaskCapability> Catalog => [.. detections.Values.Select(x => x.Descriptor), .. actions.Values.Select(x => x.Descriptor with { SupportsTimeWindow = x.SupportsTimeWindow })];
    public RuleDefinition Compile(AutomationTask task, string principal, ResourceScope scope)
    {
        Validate(task);
        var timing = actions[task.Action.CapabilityId].Timing(task.Action);
        return new(task.Id, Math.Max(1, task.Version), principal, scope,
            detections[task.Detection.CapabilityId].Compile(task.Detection, scope, task.Id),
            actions[task.Action.CapabilityId].Compile(task.Action, scope), enabled: !task.Paused,
            timing: new(timing.Debounce, timing.MinimumDuration, timing.Cooldown, timing.MaximumInputAge, task.TimeWindow));
    }
    public void Validate(AutomationTask task)
    {
        if (task.SchemaVersion != (task.TimeWindow is null ? 1 : 2) || !Guid.TryParseExact(task.Id, "D", out _) || task.Version < 0 ||
            string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 120 || task.Detection is null || task.Action is null)
            throw new ArgumentException("任务格式或版本无效。");
        if (!detections.TryGetValue(task.Detection.CapabilityId, out var detection) ||
            !actions.TryGetValue(task.Action.CapabilityId, out var action)) throw new ArgumentException("任务使用了未注册的检测或动作。");
        task.TimeWindow?.Validate();
        if (task.TimeWindow is not null && !action.SupportsTimeWindow) throw new ArgumentException("此动作暂不支持时间段限制，请关闭限制或选择一次性动作。");
        ValidateInvocation(task.Detection, detection.Descriptor);
        ValidateInvocation(task.Action, action.Descriptor);
    }
    public static void ValidateInvocation(TaskInvocation invocation, TaskCapability descriptor)
    {
        if (invocation.SchemaVersion != descriptor.SchemaVersion || invocation.CapabilityId != descriptor.Id || invocation.Parameters is null)
            throw new ArgumentException("能力配置版本不兼容。");
        if (invocation.Parameters.Count > descriptor.Fields.Length || invocation.Parameters.Keys.Any(k => !descriptor.Fields.Any(f => f.Id == k)))
            throw new ArgumentException("包含未知参数。");
        foreach (var field in descriptor.Fields)
        {
            invocation.Parameters.TryGetValue(field.Id, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required) throw new ArgumentException("请填写" + field.Label + "。");
                continue;
            }
            if (value.Length > field.MaximumLength) throw new ArgumentException(field.Label + "过长。");
            if (field.Kind == TaskFieldKind.Boolean && value is not ("true" or "false")) throw new ArgumentException(field.Label + "必须为开或关。");
            if (field.Kind == TaskFieldKind.Choice && !field.Choices.Any(x => x.Value == value)) throw new ArgumentException(field.Label + "选项无效。");
            if (field.Kind == TaskFieldKind.Number && (!decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || field.Minimum is { } min && number < min || field.Maximum is { } max && number > max))
                throw new ArgumentException(field.Label + "超出有效范围。");
        }
    }
    private static void CheckDescriptor(TaskCapability descriptor, TaskCapabilityKind kind)
    {
        if (descriptor.Kind != kind || string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.SchemaVersion < 1 ||
            descriptor.Fields.IsDefault || descriptor.Fields.Length > 32 || descriptor.Fields.Select(x => x.Id).Distinct().Count() != descriptor.Fields.Length ||
            descriptor.Fields.Any(x => string.IsNullOrWhiteSpace(x.Id) || !Enum.IsDefined(x.Kind) || x.MaximumLength < 1 ||
                x.MaximumLength > (x.Kind is TaskFieldKind.ProcessSelection or TaskFieldKind.ExecutableSelection or TaskFieldKind.TraySelection ? 32768 : 4096) || x.Choices.IsDefault))
            throw new ArgumentException("能力描述无效。");
    }
}

public sealed class AutomationTaskRouter
{
    private readonly ImmutableArray<IAutomationTaskBackend> backends;
    public AutomationTaskRouter(IEnumerable<IAutomationTaskBackend> backends)
    {
        this.backends = [.. backends];
        var combinations = this.backends.SelectMany(x => x.Combinations).ToArray();
        if (combinations.Distinct().Count() != combinations.Length) throw new ArgumentException("任务组合重复注册。");
    }
    public IAutomationTaskBackend Resolve(AutomationTask task) => backends.SingleOrDefault(x => x.Combinations.Contains(new(task.Detection.CapabilityId, task.Action.CapabilityId)))
        ?? throw new ArgumentException("这组检测与动作尚无可用的执行适配器。");
}
