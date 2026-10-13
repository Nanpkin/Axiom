using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Axiom.Domain;

public static class AutomationTaskLimits
{
    public const int MaximumTasks = 128;
}

public enum TaskCapabilityKind { Detection, Action }
public enum TaskFieldKind { Text, Boolean, Choice, Resource, TextList, FilePath, DirectoryPath, Number, ProcessSelection, ExecutableSelection, TraySelection, HotkeyList }
public sealed record TaskChoice(string Value, string Label);
public sealed record TaskField(string Id, string Label, TaskFieldKind Kind, bool Required,
    string Hint, string? DefaultValue, int MaximumLength, ImmutableArray<TaskChoice> Choices,
    decimal? Minimum = null, decimal? Maximum = null, string? Group = null, bool BrowseLocalPath = false, bool EditAsList = false);
public sealed record TaskCapability(string Id, int SchemaVersion, TaskCapabilityKind Kind,
    string Category, string Name, string Description, ImmutableArray<TaskField> Fields,
    string Behavior, string EditWarning, bool SupportsTimeWindow = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsStateCondition = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SupportsCompositeDetection = false);
public sealed record TaskInvocation(string CapabilityId, int SchemaVersion, ImmutableDictionary<string, string> Parameters);
public sealed record AutomationTask(string Id, long Version, string Title, bool Paused,
    TaskInvocation Detection, TaskInvocation Action, int SchemaVersion = 1, WeeklyTimeWindow? TimeWindow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaskDetectionExpression? DetectionExpression = null);
public sealed record TaskCombination(string DetectionId, string ActionId);
public sealed record TaskObservation(string Name, string Value, string? TaskId = null);
public sealed record AutomationSnapshot(ImmutableArray<AutomationTask> Tasks,
    ImmutableArray<TaskCapability> Capabilities, ImmutableArray<TaskCombination> Combinations,
    bool DetectionAvailable, string State, string Reason, DateTimeOffset AtUtc,
    ImmutableArray<TaskObservation> Observations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int CompositeDetectionVersion = 0);
