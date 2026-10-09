using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Pure fake operations only. No WindowsBuiltInBrightness.SetAsync or real WMI brightness mutation in tests.
public sealed class BuiltInBrightnessTests
{
    private static TaskInvocation Invocation(string percent = "30", string delay = "0") => new(BuiltInBrightnessCapabilities.Set, 1,
        new Dictionary<string, string> { ["percent"] = percent, ["delaySeconds"] = delay }.ToImmutableDictionary());
    [Fact]
    public void AllLevelsShareOneArbitratedResourceAndUseEngineDelayTimeWindowsAndRealCombinations()
    {
        var action = new BuiltInBrightnessTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal("supported-built-in-panels", action.Compile(Invocation(), scope).Key.ResourceId);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("80"), scope).Key);
        Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation(delay: "12")).MinimumDuration);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<BuiltInBrightnessPlugin>());
        foreach (var detection in new[] { SystemEventCapabilities.Event, HotkeyCapabilities.Pressed, ForegroundCapabilities.Active,
            DailyTimeCapabilities.Due, WifiProfileCapabilities.Connected, UsbDeviceCapabilities.Present })
            Assert.Contains(plugin.Combinations, c => c.DetectionId == detection && c.ActionId == BuiltInBrightnessCapabilities.Set);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        Assert.Contains("所有活动内置面板", action.Descriptor.Description); Assert.Contains("外接DDC/CI", action.Descriptor.EditWarning);
    }
    [Theory]
    [InlineData("percent", "-1")]
    [InlineData("percent", "101")]
    [InlineData("percent", "30.5")]
    [InlineData("delaySeconds", "-1")]
    [InlineData("delaySeconds", "86401")]
    public void OutOfRangeAndNonIntegerParametersAreRejected(string field, string value) =>
        Assert.Throws<ArgumentException>(() => BuiltInBrightnessCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }));
    [Fact]
    public void ArbitraryDeviceIdsAndExternalMonitorRequestsAreNotAccepted()
    {
        Assert.Throws<ArgumentException>(() => BuiltInBrightnessCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("instanceName", "synthetic") }));
        Assert.Throws<ArgumentException>(() => BuiltInBrightnessCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("ddc", "true") }));
    }
    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(6u, true)]
    [InlineData(11u, true)]
    [InlineData(13u, true)]
    [InlineData(0u, false)]
    [InlineData(5u, false)]
    [InlineData(10u, false)]
    [InlineData(12u, false)]
    [InlineData(0xffffffffu, false)]
    public void OnlyExplicitInternalConnectionTechnologiesAreEligible(uint technology, bool expected) =>
        Assert.Equal(expected, BuiltInBrightnessOperation.IsInternalConnection(technology));
    [Fact]
    public void UnknownConnectionTypeIsAnUnconfirmedPanelRatherThanClaimedExternalHardware()
    {
        foreach (var technology in new uint[] { 0xffffffff, 0xfffffffe, 42 })
            Assert.Equal(BrightnessPanelSupport.Unknown, BuiltInBrightnessOperation.ConnectionSupport(technology));
        foreach (var technology in new uint[] { 5, 10, 12 })
            Assert.Equal(BrightnessPanelSupport.Ignore, BuiltInBrightnessOperation.ConnectionSupport(technology));
        Assert.Equal(BrightnessPanelSupport.Supported, BuiltInBrightnessOperation.ConnectionSupport(11));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(100)]
    public void EveryVerifiedSupportedPanelIsSetOnceThenReadBackWhileExternalObjectsAreIgnored(int level)
    {
        var api = new FakeBrightnessApi([new("synthetic-a", BrightnessPanelSupport.Supported), new("synthetic-b", BrightnessPanelSupport.Supported),
            new("synthetic-external", BrightnessPanelSupport.Ignore)]);
        var result = BuiltInBrightnessOperation.Apply(api, level, default);
        Assert.True(result.Success); Assert.Contains("2块面板", result.Reason);
        Assert.Equal(new[] { "verify:synthetic-a", "set:synthetic-a", "read:synthetic-a", "verify:synthetic-b", "set:synthetic-b", "read:synthetic-b" }, api.Calls);
        Assert.Equal(level, api.Levels["synthetic-a"]); Assert.Equal(level, api.Levels["synthetic-b"]); Assert.False(api.Levels.ContainsKey("synthetic-external"));
    }
    [Fact]
    public void NoSupportOrUnavailableWmiReturnsFailureWithoutAnyWrites()
    {
        var absent = new FakeBrightnessApi([]); Assert.False(BuiltInBrightnessOperation.Apply(absent, 30, default).Success); Assert.Empty(absent.Calls);
        var unavailable = new FakeBrightnessApi([]) { DiscoveryError = new UnauthorizedAccessException() };
        var result = BuiltInBrightnessOperation.Apply(unavailable, 30, default);
        Assert.False(result.Success); Assert.Contains("接口不可用", result.Reason); Assert.Empty(unavailable.Calls);
    }
    [Fact]
    public void UnknownUnsupportedAndAmbiguousIdentitiesAreSkippedAndReportedWithoutGuessingTargets()
    {
        var api = new FakeBrightnessApi([new("ambiguous", BrightnessPanelSupport.Supported), new("AMBIGUOUS", BrightnessPanelSupport.Supported),
            new("unknown", BrightnessPanelSupport.Unknown), new("unsupported", BrightnessPanelSupport.Unsupported), new("good", BrightnessPanelSupport.Supported)]);
        var result = BuiltInBrightnessOperation.Apply(api, 30, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("4块接口缺失", result.Reason);
        Assert.Equal(new[] { "verify:good", "set:good", "read:good" }, api.Calls);
        Assert.DoesNotContain("ambiguous", result.Reason, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("unknown", result.Reason);
    }
    [Fact]
    public void ChangedIdentityOrDriverRefusalDoesNotBlockOtherPanelAndNeverRetries()
    {
        var api = new FakeBrightnessApi([new("changed", BrightnessPanelSupport.Supported), new("denied", BrightnessPanelSupport.Supported), new("good", BrightnessPanelSupport.Supported)]);
        api.FailedVerification.Add("changed"); api.SetErrors["denied"] = 5;
        var result = BuiltInBrightnessOperation.Apply(api, 30, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("2块写入/读回失败", result.Reason);
        Assert.DoesNotContain("set:changed", api.Calls); Assert.Single(api.Calls, c => c == "set:denied"); Assert.DoesNotContain("read:denied", api.Calls);
        Assert.Equal(30, api.Levels["good"]);
    }
    [Fact]
    public void ApproximateOrUnavailableReadbackIsUnconfirmedWithoutFallbackRetryOrRestoration()
    {
        var api = new FakeBrightnessApi([new("rounded", BrightnessPanelSupport.Supported), new("unreadable", BrightnessPanelSupport.Supported)]);
        api.Readbacks["rounded"] = 35; api.Readbacks["unreadable"] = null;
        var result = BuiltInBrightnessOperation.Apply(api, 30, default);
        Assert.False(result.Success); Assert.Contains("未确认", result.Reason); Assert.Contains("2块写入/读回失败", result.Reason);
        Assert.Equal(30, api.Levels["rounded"]); Assert.Equal(30, api.Levels["unreadable"]);
        Assert.Equal(2, api.Calls.Count(c => c.StartsWith("set:", StringComparison.Ordinal)));
    }
    [Fact]
    public void InvalidPercentAndOversizedDiscoveryNeverWrite()
    {
        var untouched = new FakeBrightnessApi([]);
        Assert.Throws<ArgumentException>(() => BuiltInBrightnessOperation.Apply(untouched, 101, default)); Assert.Equal(0, untouched.Discoveries);
        var tooMany = new FakeBrightnessApi(Enumerable.Range(0, 129).Select(i => new BrightnessPanel("synthetic-" + i, BrightnessPanelSupport.Supported)).ToArray());
        Assert.False(BuiltInBrightnessOperation.Apply(tooMany, 30, default).Success); Assert.Empty(tooMany.Calls);
    }
    [Fact]
    public void CancellationBeforeWriteAndAfterFirstWriteStopsLaterPanelsWithoutRestoringTheChangedOne()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var untouched = new FakeBrightnessApi([new("first", BrightnessPanelSupport.Supported)]);
        Assert.ThrowsAny<OperationCanceledException>(() => BuiltInBrightnessOperation.Apply(untouched, 30, stop.Token)); Assert.Equal(0, untouched.Discoveries);
        using var verifying = new CancellationTokenSource(); var canceled = new FakeBrightnessApi([new("first", BrightnessPanelSupport.Supported)]) { AfterVerify = verifying.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => BuiltInBrightnessOperation.Apply(canceled, 30, verifying.Token)); Assert.DoesNotContain("set:first", canceled.Calls);
        using var after = new CancellationTokenSource(); var changed = new FakeBrightnessApi([new("first", BrightnessPanelSupport.Supported), new("later", BrightnessPanelSupport.Supported)]) { AfterSet = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => BuiltInBrightnessOperation.Apply(changed, 30, after.Token));
        Assert.Equal(30, changed.Levels["first"]); Assert.False(changed.Levels.ContainsKey("later")); Assert.DoesNotContain("read:first", changed.Calls);
    }
    [Fact(Timeout = 10000)]
    public async Task CanceledNativeWriteKeepsAdmissionAndDrainUntilReturnAndNeverWritesTheNextPanel()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var commands = new BoundedNativeCommand("内置屏幕亮度", TimeSpan.FromSeconds(5));
        var api = new FakeBrightnessApi([new("first", BrightnessPanelSupport.Supported), new("later", BrightnessPanelSupport.Supported)])
            { AfterSet = () => { entered.SetResult(); release.Wait(); } };
        var request = commands.RunAsync(token => BuiltInBrightnessOperation.Apply(api, 30, token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); Assert.False(commands.DrainAsync().IsCompleted);
            var overlap = await commands.RunAsync(token => BuiltInBrightnessOperation.Apply(api, 30, token), default);
            Assert.False(overlap.Success); Assert.Contains("本次未执行", overlap.Reason);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(30, api.Levels["first"]); Assert.False(api.Levels.ContainsKey("later")); Assert.Single(api.Calls, c => c.StartsWith("set:", StringComparison.Ordinal));
    }
    private sealed class FakeBrightnessApi(BrightnessPanel[] panels) : IBuiltInBrightnessApi
    {
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, int> Levels = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, uint> SetErrors = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int?> Readbacks = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> FailedVerification = new(StringComparer.OrdinalIgnoreCase);
        internal int Discoveries; internal Exception? DiscoveryError; internal Action? AfterVerify, AfterSet;
        public IReadOnlyList<BrightnessPanel> FindPanels() { Discoveries++; if (DiscoveryError is not null) throw DiscoveryError; return panels; }
        public bool Verify(string key) { Calls.Add("verify:" + key); AfterVerify?.Invoke(); return !FailedVerification.Contains(key); }
        public uint Set(string key, byte value) { Calls.Add("set:" + key); if (SetErrors.TryGetValue(key, out var error)) return error; Levels[key] = value; AfterSet?.Invoke(); return 0; }
        public int? Read(string key) { Calls.Add("read:" + key); return Readbacks.TryGetValue(key, out var value) ? value : Levels[key]; }
    }
}
