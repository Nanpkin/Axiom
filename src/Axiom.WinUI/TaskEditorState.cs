using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.WinUI;

internal sealed class TaskEditorState
{
    public string Id { get; private set; } = Guid.NewGuid().ToString("D");
    public long Version { get; private set; }
    public string Title { get; set; } = "";
    public bool Paused { get; set; }
    public WeeklyTimeWindow? TimeWindow { get; set; }
    public TaskInvocation? Detection { get; private set; }
    public TaskInvocation? Action { get; private set; }
    private AutomationTask? original;
    public bool Complete => Detection is not null && Action is not null;
    public bool Dirty => original is null ? Title.Length > 0 || Detection is not null || Action is not null || Paused || TimeWindow is not null :
        Title != original.Title || Paused != original.Paused || TimeWindow != original.TimeWindow || !Same(Detection, original.Detection) || !Same(Action, original.Action);
    private static bool Same(TaskInvocation? a, TaskInvocation b) => a is not null && a.CapabilityId == b.CapabilityId && a.SchemaVersion == b.SchemaVersion &&
        a.Parameters.Count == b.Parameters.Count && a.Parameters.All(x => b.Parameters.TryGetValue(x.Key, out var value) && value == x.Value);
    public void Load(AutomationTask? task)
    {
        original = task; Id = task?.Id ?? Guid.NewGuid().ToString("D"); Version = task?.Version ?? 0;
        TimeWindow = task?.TimeWindow;
        Title = task?.Title ?? ""; Paused = task?.Paused ?? false; Detection = task?.Detection; Action = task?.Action;
    }
    public void LoadDraft(AutomationTask task)
    {
        if (task.Version != 0) throw new ArgumentException("候选必须是未保存的任务。");
        Load(task); original = null;
    }
    public void Select(TaskCapability descriptor, ImmutableArray<TaskCombination> combinations)
    {
        var invocation = new TaskInvocation(descriptor.Id, descriptor.SchemaVersion,
            descriptor.Fields.Where(x => x.DefaultValue is not null || x.Kind == TaskFieldKind.Boolean)
                .ToImmutableDictionary(x => x.Id, x => x.DefaultValue ?? "false"));
        if (descriptor.Kind == TaskCapabilityKind.Detection)
        {
            if (Detection?.CapabilityId == descriptor.Id) return;
            Detection = invocation;
            if (Action is not null && !combinations.Contains(new(descriptor.Id, Action.CapabilityId))) Action = null;
        }
        else
        {
            if (Detection is not null && !combinations.Contains(new(Detection.CapabilityId, descriptor.Id))) throw new ArgumentException("检测与动作不兼容。");
            if (Action?.CapabilityId == descriptor.Id) return;
            Action = invocation;
        }
    }
    public void SetParameter(TaskCapabilityKind kind, string key, string value)
    {
        if (kind == TaskCapabilityKind.Detection && Detection is not null) Detection = Detection with { Parameters = Detection.Parameters.SetItem(key, value) };
        if (kind == TaskCapabilityKind.Action && Action is not null) Action = Action with { Parameters = Action.Parameters.SetItem(key, value) };
    }
    public AutomationTask Build() => Complete ? new(Id, Version, Title.Trim(), Paused, Detection!, Action!, SchemaVersion: TimeWindow is null ? 1 : 2, TimeWindow: TimeWindow) : throw new InvalidOperationException("请选择检测和动作类型。");
}
