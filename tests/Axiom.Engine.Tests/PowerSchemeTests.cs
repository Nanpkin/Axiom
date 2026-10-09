using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class PowerSchemeTests
{
    private static TaskInvocation Invocation(string scheme = "balanced", string delay = "0") => new(PowerSchemeCapabilities.Set, 1,
        new Dictionary<string, string> { ["scheme"] = scheme, ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void AllChoicesUseOneArbitratedResourceAndSupportTimeWindowsWithRealEventCombinations()
    {
        var action = new PowerSchemeTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal("active-power-scheme", action.Compile(Invocation(), scope).Key.ResourceId);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("saver"), scope).Key);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("performance"), scope).Key);
        Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation(delay: "12")).MinimumDuration);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<PowerSchemePlugin>());
        foreach (var detection in new[] { SystemEventCapabilities.Event, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
            WindowPresenceCapabilities.Absent, WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected })
            Assert.Contains(plugin.Combinations, c => c.DetectionId == detection && c.ActionId == PowerSchemeCapabilities.Set);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
    }

    [Theory]
    [InlineData("scheme", "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("scheme", "ultimate")]
    [InlineData("scheme", "BALANCED")]
    [InlineData("delaySeconds", "-1")]
    [InlineData("delaySeconds", "1.5")]
    [InlineData("delaySeconds", "86401")]
    public void UnsupportedChoicesAndInvalidDelayAreRejectedBeforeNativeExecution(string field, string value) =>
        Assert.Throws<ArgumentException>(() => PowerSchemeCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }));

    [Fact]
    public void KnownStandardGuidMappingAndStrictFieldSetPreventArbitrarySchemeOrImportRequests()
    {
        Assert.Equal(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), PowerSchemeCapabilities.Validate(Invocation()).Scheme);
        Assert.Equal(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), PowerSchemeCapabilities.Validate(Invocation("saver")).Scheme);
        Assert.Equal(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), PowerSchemeCapabilities.Validate(Invocation("performance")).Scheme);
        Assert.Throws<ArgumentException>(() => PowerSchemeCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("create", "true") }));
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("saver")]
    [InlineData("performance")]
    public void ExistingStandardSchemeIsSetOnceThenReadBackWithoutTouchingOtherSchemeSettings(string selected)
    {
        var settings = PowerSchemeCapabilities.Validate(Invocation(selected));
        var api = new FakePowerSchemeApi([Guid.NewGuid(), settings.Scheme, Guid.NewGuid()]);
        var result = PowerSchemeOperation.Apply(api, settings.Scheme, settings.Name, default);
        Assert.True(result.Success); Assert.Contains(settings.Name, result.Reason); Assert.Contains("不恢复", result.Reason);
        Assert.Equal(settings.Scheme, api.Active); Assert.Equal(new[] { "enumerate:0", "enumerate:1", "set", "get" }, api.Calls);
        Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.GetCalls);
    }

    [Fact]
    public void MissingSchemeAndEnumerationFailureNeverCallSetOrReadback()
    {
        var missing = new FakePowerSchemeApi([PowerSchemeCapabilities.Balanced]);
        var result = PowerSchemeOperation.Apply(missing, PowerSchemeCapabilities.Saver, "节能", default);
        Assert.False(result.Success); Assert.Contains("没有标准节能", result.Reason); Assert.Contains("Modern Standby", result.Reason);
        Assert.Equal(0, missing.SetCalls); Assert.Equal(0, missing.GetCalls);
        var unavailable = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { EnumerateError = 5 };
        Assert.False(PowerSchemeOperation.Apply(unavailable, PowerSchemeCapabilities.Saver, "节能", default).Success);
        Assert.Equal(0, unavailable.SetCalls); Assert.Equal(0, unavailable.GetCalls);
    }

    [Fact]
    public void EnumerationIsBoundedAndRejectsExcessWithoutChangingTheCurrentScheme()
    {
        var api = new FakePowerSchemeApi(Enumerable.Repeat(PowerSchemeCapabilities.Balanced, 4097).ToArray());
        var result = PowerSchemeOperation.Apply(api, PowerSchemeCapabilities.Saver, "节能", default);
        Assert.False(result.Success); Assert.Contains("超出", result.Reason); Assert.Equal(4096, api.Calls.Count);
        Assert.Equal(0, api.SetCalls); Assert.Equal(0, api.GetCalls);
    }

    [Fact]
    public void SetFailureDoesNotRetryAndReadbackMismatchDoesNotRestoreOverExternalChanges()
    {
        var refused = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { SetError = 5 };
        var result = PowerSchemeOperation.Apply(refused, PowerSchemeCapabilities.Saver, "节能", default);
        Assert.False(result.Success); Assert.Contains("拒绝", result.Reason); Assert.Equal(1, refused.SetCalls); Assert.Equal(0, refused.GetCalls);
        var external = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { AfterSet = api => api.Active = PowerSchemeCapabilities.Performance };
        result = PowerSchemeOperation.Apply(external, PowerSchemeCapabilities.Saver, "节能", default);
        Assert.False(result.Success); Assert.Contains("读回不一致", result.Reason); Assert.Equal(PowerSchemeCapabilities.Performance, external.Active);
        Assert.Equal(1, external.SetCalls); Assert.Equal(1, external.GetCalls);
    }

    [Fact]
    public void ReadbackFailureReportsUnconfirmedInsteadOfRestoringOrResendingTheScheme()
    {
        var api = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { GetError = 5 };
        var result = PowerSchemeOperation.Apply(api, PowerSchemeCapabilities.Saver, "节能", default);
        Assert.False(result.Success); Assert.Contains("结果未确认", result.Reason);
        Assert.Equal(PowerSchemeCapabilities.Saver, api.Active); Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.GetCalls);
    }

    [Fact]
    public void CancellationBeforeOrDuringEnumerationNeverWritesAndCancellationAfterSetLeavesTheNewScheme()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var untouched = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]);
        Assert.ThrowsAny<OperationCanceledException>(() => PowerSchemeOperation.Apply(untouched, PowerSchemeCapabilities.Saver, "节能", before.Token)); Assert.Empty(untouched.Calls);
        using var enumerate = new CancellationTokenSource(); var canceled = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { AfterEnumerate = enumerate.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => PowerSchemeOperation.Apply(canceled, PowerSchemeCapabilities.Saver, "节能", enumerate.Token)); Assert.Equal(0, canceled.SetCalls);
        using var after = new CancellationTokenSource(); var changed = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { AfterSet = _ => after.Cancel() };
        Assert.ThrowsAny<OperationCanceledException>(() => PowerSchemeOperation.Apply(changed, PowerSchemeCapabilities.Saver, "节能", after.Token));
        Assert.Equal(PowerSchemeCapabilities.Saver, changed.Active); Assert.Equal(1, changed.SetCalls); Assert.Equal(0, changed.GetCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task CanceledNativeSwitchRetainsAdmissionAndDrainUntilItReturnsWithoutRestoringTheOldScheme()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var commands = new BoundedNativeCommand("电源方案", TimeSpan.FromSeconds(5));
        var api = new FakePowerSchemeApi([PowerSchemeCapabilities.Saver]) { AfterSet = _ => { entered.SetResult(); release.Wait(); } };
        var request = commands.RunAsync(token => PowerSchemeOperation.Apply(api, PowerSchemeCapabilities.Saver, "节能", token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(commands.DrainAsync().IsCompleted);
            var overlapping = await commands.RunAsync(token => PowerSchemeOperation.Apply(api, PowerSchemeCapabilities.Saver, "节能", token), default);
            Assert.False(overlapping.Success); Assert.Contains("本次未执行", overlapping.Reason); Assert.Equal(1, api.SetCalls);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(PowerSchemeCapabilities.Saver, api.Active); Assert.Equal(1, api.SetCalls); Assert.Equal(0, api.GetCalls);
    }

    private sealed class FakePowerSchemeApi(Guid[] schemes) : IPowerSchemeApi
    {
        internal readonly List<string> Calls = [];
        internal Guid Active = PowerSchemeCapabilities.Balanced;
        internal uint EnumerateError, SetError, GetError;
        internal int SetCalls, GetCalls;
        internal Action? AfterEnumerate;
        internal Action<FakePowerSchemeApi>? AfterSet;
        public uint Enumerate(uint index, out Guid scheme)
        {
            Calls.Add("enumerate:" + index); scheme = index < schemes.Length ? schemes[index] : default; AfterEnumerate?.Invoke();
            return EnumerateError != 0 ? EnumerateError : index < schemes.Length ? 0u : 259u;
        }
        public uint SetActive(Guid scheme)
        {
            Calls.Add("set"); SetCalls++; if (SetError != 0) return SetError;
            Active = scheme; AfterSet?.Invoke(this); return 0;
        }
        public uint GetActive(out Guid scheme) { Calls.Add("get"); GetCalls++; scheme = Active; return GetError; }
    }
}
