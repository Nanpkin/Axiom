using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class TaskbarAutoHideRestorePlugin : IAutomationPlugin
{
    public string Id => "axiom.taskbar-auto-hide-restore";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new RestorableSettingTaskAction(TaskbarAutoHideRestoreCapabilities.Descriptor,
        TaskbarAutoHideRestoreCapabilities.Validate, TaskbarAutoHideRestoreCapabilities.ResourceId)];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", ForegroundCapabilities.Active,
        WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent, WindowStateDetectionCapabilities.StateIs,
        DisplayCountCapabilities.Count, PowerStateCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State,
        MediaPlaybackCapabilities.State, ApplicationAudioActivityCapabilities.Active, NetworkCategoryCapabilities.State, InternetConnectivityCapabilities.State,
        WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected }
        .Select(d => new TaskCombination(d, TaskbarAutoHideRestoreCapabilities.Restore))];
    public IEnumerable<CapabilityBinding> Bindings => [RestorableSettingTaskAction.Binding(TaskbarAutoHideRestoreCapabilities.Restore)];
}
