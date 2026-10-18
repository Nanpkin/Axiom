using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class BuiltInBrightnessRestoreCapabilityProvider(string sid) : RestorableSettingCapabilityProvider(
    BuiltInBrightnessRestoreCapabilities.Set, (_, token) => WindowsBuiltInBrightness.CreateRestorableSessionAsync(sid, token),
    BuiltInBrightnessRestoreCapabilities.Validate, _ => "brightness", TimeSpan.FromSeconds(30));

[SupportedOSPlatform("windows")]
internal sealed class HardwareBrightnessRestoreCapabilityProvider(string sid) : RestorableSettingCapabilityProvider(
    HardwareBrightnessRestoreCapabilities.Set, (parameters, token) => WindowsHardwareBrightness.CreateRestorableSessionAsync(parameters["target"], sid, token),
    HardwareBrightnessRestoreCapabilities.Validate, _ => "brightness", TimeSpan.FromSeconds(30));
