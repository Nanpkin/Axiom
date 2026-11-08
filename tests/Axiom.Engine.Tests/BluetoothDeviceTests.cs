using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class BluetoothDeviceTests
{
    private static TaskInvocation Detection(bool disconnected = false, string names = "Synthetic Controller") => new(
        disconnected ? BluetoothDeviceCapabilities.Disconnected : BluetoothDeviceCapabilities.Connected, 1,
        ImmutableDictionary<string, string>.Empty.Add("deviceNames", names));

    [Fact]
    public void FullNamesAreNormalizedAndCompilationRequiresStrictParameters()
    {
        Assert.Equal(new[] { "SYNTHETIC CONTROLLER", "SECOND" }, BluetoothDeviceCapabilities.Parse("Synthetic Controller\r\nsynthetic controller\n Second "));
        foreach (var names in new[] { "", "*controller*", "Controller?", "Controller\tSuffix", new string('x', 256), string.Join('\n', Enumerable.Range(0, 17)) })
            Assert.Throws<ArgumentException>(() => BluetoothDeviceCapabilities.Parse(names));
        var compiler = new BluetoothDeviceTaskDetection(false);
        Assert.Equal("SYNTHETIC CONTROLLER", Assert.Single(compiler.Compile(Detection(), ResourceScope.Machine).Dependencies).ResourceId);
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { SchemaVersion = 2 }, ResourceScope.Machine));
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { Parameters = Detection().Parameters.Add("extra", "x") }, ResourceScope.Machine));
        Assert.Equal(ConditionKind.Any, compiler.Compile(Detection(names: "First\nSecond"), ResourceScope.Machine).Kind);
        Assert.Equal(ConditionKind.All, new BluetoothDeviceTaskDetection(true).Compile(Detection(true, "First\nSecond"), ResourceScope.Machine).Kind);
        var combinations = new BluetoothDevicePlugin().Combinations;
        Assert.Contains(new(BluetoothDeviceCapabilities.Connected, TrayCapabilities.Show), combinations);
        Assert.Contains(new(BluetoothDeviceCapabilities.Disconnected, TrayCapabilities.Hide), combinations);
    }

    [Fact(Timeout = 10000)]
    public async Task PairedIsNotConnectedAndSameNamesAreAGroupIndependentOfHidVisibility()
    {
        var queue = Channel.CreateUnbounded<BluetoothDeviceChange>();
        var updates = Channel.CreateUnbounded<(BluetoothDeviceValue[] Values, bool Baseline)>();
        using var stop = new CancellationTokenSource();
        var run = BluetoothDeviceObservation.RunAsync(["SYNTHETIC CONTROLLER"], queue.Reader, (v, b) => updates.Writer.TryWrite((v, b)), stop.Token);
        async Task<(bool? Connected, bool Baseline)> Next()
        {
            var next = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            return (Assert.Single(next.Values).Connected, next.Baseline);
        }
        void Set(string id, bool? connected, string name = "Synthetic Controller") => queue.Writer.TryWrite(new(new(id, name, connected)));
        Set("physical-aep-a", false); Assert.False(updates.Reader.TryRead(out _));
        queue.Writer.TryWrite(new(EnumerationCompleted: true)); Assert.Equal((false, true), await Next());
        Set("physical-aep-a", true); Assert.Equal((true, false), await Next());
        // DSX hides HID and creates a virtual wired device. Neither operation changes the physical AEP connection.
        Set("physical-aep-a", true); Set("physical-aep-b", true);
        Set("physical-aep-a", false); Set("physical-aep-b", false); Assert.Equal((false, false), await Next());
        Set("other-name", true, "Synthetic Controller Extra");
        Set("physical-aep-a", null); Assert.Equal(((bool?)null, false), await Next());
        Set("physical-aep-a", true); Assert.Equal((true, true), await Next());
        queue.Writer.TryWrite(new(RemovedId: "physical-aep-a")); Assert.Equal((false, false), await Next());
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task IncompleteEndpointMetadataCannotBecomeFalseAndQueueFailureIsNotDisconnection()
    {
        var queue = Channel.CreateUnbounded<BluetoothDeviceChange>();
        var updates = Channel.CreateUnbounded<BluetoothDeviceValue[]>();
        using var stop = new CancellationTokenSource();
        var run = BluetoothDeviceObservation.RunAsync(["CONTROLLER"], queue.Reader, (v, _) => updates.Writer.TryWrite(v), stop.Token);
        queue.Writer.TryWrite(new(new("unnamed", "", true))); queue.Writer.TryWrite(new(EnumerationCompleted: true));
        Assert.Null(Assert.Single(await updates.Reader.ReadAsync()).Connected);
        queue.Writer.TryComplete(new IOException("synthetic notification loss"));
        await Assert.ThrowsAsync<IOException>(() => run); Assert.False(updates.Reader.TryRead(out _));
    }

    [Fact(Timeout = 10000)]
    public async Task ProviderPublishesUnknownWhenNativeSubscriptionFailsAndSupportsCancellation()
    {
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        using var stop = new CancellationTokenSource();
        var provider = new BluetoothDeviceCapabilityProvider((names, publish, token) =>
        {
            publish([new(names[0], true)], true); throw new IOException("synthetic failure with private identity");
        });
        var run = provider.ObserveAsync([new(BluetoothDeviceCapabilities.Connected, "CONTROLLER")], update => updates.Writer.TryWrite(update), stop.Token);
        Assert.True(Assert.Single((await updates.Reader.ReadAsync()).Samples).Boolean);
        var unknown = Assert.Single((await updates.Reader.ReadAsync()).Samples);
        Assert.Equal(StateQuality.Unknown, unknown.Quality); Assert.Null(unknown.Boolean);
        Assert.DoesNotContain("private", unknown.Reason);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void CatalogImportsLeaveDeviceNamesLocalAndEditorCanComposeWithTrayActions()
    {
        var compiler = new AutomationTaskCompiler(supportsCompositeDetection: true);
        foreach (var plugin in BuiltInAutomationPlugins.Create())
        {
            foreach (var detection in plugin.Detections) compiler.Register(detection);
            foreach (var action in plugin.Actions) compiler.Register(action);
        }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. BuiltInAutomationPlugins.Create().SelectMany(p => p.Combinations)], true, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, []);
        var recorder = new UsageRecorder(new(5, true, false, []), DateTimeOffset.UnixEpoch);
        var actionInvocation = new TaskInvocation(NotificationCapabilities.Show, 1, ImmutableDictionary<string, string>.Empty.Add("title", "").Add("body", "").Add("sound", "false").Add("delaySeconds", "0"));
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "synthetic", "synthetic", Detection(names: ""), actionInvocation);
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Contains("蓝牙设备名称", UsageAnalysis.MissingManualFields(draft, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(names: "Private Controller") }), recorder.Session, catalog));
        var editor = new Axiom.WinUI.TaskEditorState(); editor.Load(null); editor.Title = "Synthetic Bluetooth tray";
        editor.Select(BluetoothDeviceCapabilities.ConnectedDescriptor, catalog.Combinations);
        editor.SetParameter(TaskCapabilityKind.Detection, "deviceNames", "Synthetic Controller");
        editor.Select(TrayCapabilities.ShowDescriptor, catalog.Combinations);
        editor.SetParameter(TaskCapabilityKind.Action, "executable", @"C:\Apps\SyntheticTray.exe");
        Assert.Equal(TrayCapabilities.Show, compiler.Compile(editor.Build(), "owner", ResourceScope.Session("owner", 1)).Action.Key.CapabilityId);
        Assert.Contains(catalog.Capabilities, c => c.Id == TrayCapabilities.Show && c.SupportsCompositeDetection);
        Assert.Contains(catalog.Capabilities, c => c.Id == BluetoothDeviceCapabilities.Connected && c.IsStateCondition);
    }
}
