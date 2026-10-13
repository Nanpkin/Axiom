using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class TaskbarAutoHideRestoreCapabilityProvider(string sid) : RestorableSettingCapabilityProvider(
    TaskbarAutoHideRestoreCapabilities.Restore,
    (parameters, token) => WindowsTaskbarAutoHide.OpenRestoreAsync(parameters, sid, token),
    TaskbarAutoHideRestoreCapabilities.Validate, TaskbarAutoHideRestoreCapabilities.ResourceId);
