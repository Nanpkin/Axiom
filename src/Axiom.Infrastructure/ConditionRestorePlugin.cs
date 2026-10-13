using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ConditionRestorePlugin : IAutomationPlugin
{
    public string Id => "axiom.condition-restore";
    private static TaskCapability[] Descriptors => [ConditionRestoreCapabilities.PlaybackDescriptor, ConditionRestoreCapabilities.MicrophoneDescriptor, ConditionRestoreCapabilities.PowerSchemeDescriptor,
        ConditionRestoreCapabilities.ProgramPriorityDescriptor, ConditionRestoreCapabilities.WindowStateDescriptor];
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => Descriptors.Select(d => new RestorableSettingTaskAction(d, ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId));
    public static string[] StateDetections => ["process.running", "process.exited", ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent,
        WindowStateDetectionCapabilities.StateIs, DisplayCountCapabilities.Count, PowerStateCapabilities.State, MediaPlaybackCapabilities.State,
        NetworkCategoryCapabilities.State, InternetConnectivityCapabilities.State, WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected,
        SessionDisplayCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State, EndpointMuteCapabilities.State];
    public ImmutableArray<TaskCombination> Combinations => [.. Descriptors.SelectMany(a => StateDetections
        .Where(d => a.Id != ConditionRestoreCapabilities.PowerScheme || d != PowerSchemeStateCapabilities.State)
        .Where(d => a.Id != ConditionRestoreCapabilities.WindowState || d is not (WindowStateDetectionCapabilities.StateIs or ForegroundCapabilities.Active))
        .Where(d => a.Id is not (ConditionRestoreCapabilities.Playback or ConditionRestoreCapabilities.Microphone) || d != EndpointMuteCapabilities.State)
        .Select(d => new TaskCombination(d, a.Id))), new("session.lock-state", ConditionRestoreCapabilities.Playback)];
    public IEnumerable<CapabilityBinding> Bindings => Descriptors.Select(d => RestorableSettingTaskAction.Binding(d.Id));
}
