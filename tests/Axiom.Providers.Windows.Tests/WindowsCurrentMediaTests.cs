using Axiom.Providers.Windows;

namespace Axiom.Providers.Windows.Tests;

// Windows-only project. These tests exercise real GSMTC access from an unpackaged
// desktop process. No media request method, title/track metadata or source ID is read.
public sealed class WindowsCurrentMediaTests
{
    [Fact(Timeout = 15000)]
    public async Task UnpackagedDesktopCanRequestManagerAndReadItsCurrentSessionWithoutCommands()
    {
        Assert.True(OperatingSystem.IsWindows(), "This native suite must run on Windows.");
        using var api = await WindowsCurrentMedia.OpenApiAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var current = api.CaptureCurrent();
        if (current is null) return; // A CI desktop normally has no media; access must still succeed.
        _ = current.ReadPlayback();
        _ = api.IsCurrent(current); // Real current selection may legitimately change during this read.
    }

    [Fact(Timeout = 15000)]
    public async Task SeparateManagerWrappersNeverAcceptAnotherManagersCapturedSession()
    {
        Assert.True(OperatingSystem.IsWindows(), "This native suite must run on Windows.");
        using var first = await WindowsCurrentMedia.OpenApiAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var second = await WindowsCurrentMedia.OpenApiAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var captured = first.CaptureCurrent();
        if (captured is null) return;
        Assert.False(second.IsCurrent(captured));
        for (var i = 0; i < 16; i++) _ = first.IsCurrent(captured); // QueryInterface references are released on every read.
    }
}
