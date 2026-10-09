using Axiom.Engine;

namespace Axiom.Infrastructure;

// Shared by the Host composition root and complete-catalog export/template checks.
// HDR retains its separate Desired State lifecycle adapter.
public static class BuiltInAutomationPlugins
{
    public static IAutomationPlugin[] Create() =>
    [
        new SessionDisplayPlugin(), new DisplayCountPlugin(), new InternetConnectivityPlugin(), new ProgramPlugin(), new AxiomStartupPlugin(), new WindowPlugin(), new TrayPlugin(), new KeyboardPlugin(),
        new SystemEventPlugin(), new IntervalPlugin(), new DailyTimePlugin(), new WindowedIntervalPlugin(), new FileArrivalPlugin(), new AudioPlaybackPlugin(),
        new PowerStatePlugin(), new BuiltInBrightnessPlugin(), new EndpointMutePlugin(), new SessionLockPlugin(), new WindowMonitorPlugin(), new UsbDevicePlugin(), new PowerSchemePlugin(), new WifiProfilePlugin(), new KeepAwakePlugin(), new ApplicationAudioPlugin(), new MicrophonePlugin(), new NotificationPlugin(), new OnceTimePlugin(), new AudioPlugin(), new OpenTargetPlugin(), new ForegroundPlugin(), new WindowPresencePlugin(), new WindowStateDetectionPlugin()
    ];
}
