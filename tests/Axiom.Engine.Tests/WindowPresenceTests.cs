using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class WindowPresenceTests
{
    [Fact]
    public void LastVisibleWindowControlsPresenceAndUnknownDoesNotImplyAbsence()
    {
        var state = new WindowPresenceState(["Editor.exe", "Browser.exe"]);
        state.Set(1, true, "Editor.exe", true); state.Set(2, true, "editor.exe", true);
        Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(1, false, null, true);
        Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(2, false, null, true);
        Assert.False(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(3, true, "Editor.exe", false);
        Assert.Null(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        Assert.False(state.Samples().Single(s => s.ProcessName == "Browser.exe").Visible);
        state.Set(4, true, "Editor.exe", true); Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(3, false, null, true); state.Set(4, false, null, true); state.Set(5, true, null, false);
        Assert.All(state.Samples(), s => Assert.Null(s.Visible));
    }
    [Fact]
    public void IdentityChangesReplaceCachedWindowAndPositiveSamplesPrecedeNegativeOnes()
    {
        var state = new WindowPresenceState(["A.exe", "B.exe"]);
        state.Set(1, true, "A.exe", true); state.Set(1, true, "B.exe", true);
        var samples = state.Samples(); Assert.Equal("B.exe", samples[0].ProcessName); Assert.True(samples[0].Visible); Assert.False(samples[1].Visible);
        state.Set(1, true, "Unrelated.exe", true); Assert.All(state.Samples(), s => Assert.False(s.Visible));
    }
    [Fact]
    public void WindowTrackingFailsExplicitlyAtCapacityAndRemovalFreesCapacity()
    {
        var state = new WindowPresenceState(["A.exe"]);
        for (var i = 1; i <= 4096; i++) state.Set(i, true, "A.exe", true);
        Assert.Throws<IOException>(() => state.Set(4097, true, "A.exe", true));
        state.Set(1, false, null, true); state.Set(4097, true, "A.exe", true); Assert.True(state.Samples().Single().Visible);
    }
    [Fact]
    public void WindowDetectionCompilesWithWindowActionAndRejectsPathAsProcessName()
    {
        var compiler = new WindowPresenceTaskDetection();
        var invocation = new TaskInvocation(WindowPresenceCapabilities.Visible, 1, ImmutableDictionary<string,string>.Empty.Add("processNames", "A.exe\nB.exe"));
        Assert.Equal(2, compiler.Compile(invocation, ResourceScope.Session("owner", 1)).Dependencies.Count);
        Assert.Contains(new WindowPresencePlugin().Combinations, c => c.ActionId == WindowCapabilities.State);
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("processNames", "C:\\Apps\\A.exe") }, ResourceScope.Session("owner", 1)));
    }
}
