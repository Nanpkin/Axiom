using System.Text;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class SteamCatalogTests
{
    [Fact]
    public void TextVdfHandlesEscapedPathsCommentsNestedAndLegacyLibrariesAndRejectsTruncation()
    {
        var root = SteamKeyValues.Parse("// comment\n\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\SteamLibrary\" } \"1\" \"E:\\\\Steam\" }");
        Assert.Equal(@"D:\SteamLibrary", root.Get("libraryfolders")!.Get("0")!.Text("path"));
        Assert.Equal(@"E:\Steam", root.Get("libraryfolders")!.Text("1"));
        Assert.Equal("}", SteamKeyValues.Parse("AppState { name \"}\" }").Get("AppState")!.Text("name"));
        Assert.Throws<InvalidDataException>(() => SteamKeyValues.Parse("\"AppState\" { \"name\" \"bad"));
        Assert.Throws<InvalidDataException>(() => SteamKeyValues.Parse("{ }"));
    }
    [Theory]
    [InlineData(0x07564427u)]
    [InlineData(0x07564428u)]
    [InlineData(0x07564429u)]
    public void AppInfoReadsWindowsLaunchMetadataAndSkipsUninstalledEntries(uint magic)
    {
        using var bytes = AppInfo(magic);
        var apps = SteamAppInfo.Read(bytes, new HashSet<uint> { 730 }, CancellationToken.None);
        Assert.Equal("bin\\win64\\cs2.exe", Assert.Single(apps).Value.Get("appinfo")!.Get("config")!.Get("launch")!.Get("0")!.Text("executable"));
        bytes.Position = 0; Assert.Empty(SteamAppInfo.Read(bytes, new HashSet<uint>(), CancellationToken.None));
    }
    [Fact]
    public void AppInfoRejectsUnsupportedFormatsAndTruncatedEntries()
    {
        using var bytes = AppInfo(0x07564429); var all = bytes.ToArray();
        using var truncated = new MemoryStream(all[..30]);
        Assert.Throws<InvalidDataException>(() => SteamAppInfo.Read(truncated, new HashSet<uint> { 730 }, CancellationToken.None));
        all[0] = 0; using var unknown = new MemoryStream(all);
        Assert.Throws<InvalidDataException>(() => SteamAppInfo.Read(unknown, new HashSet<uint> { 730 }, CancellationToken.None));
    }
    [Fact]
    public void LaunchResolutionUsesOnlyDeclaredExactFilesRejectsEscapesAndIgnoresLinux()
    {
        using var fixture = new Fixture();
        var install = SteamGameMetadata.InstallationPath(fixture.Root, "TestGame");
        Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "game.exe"), "");
        File.WriteAllText(Path.Combine(install, "other.exe"), "");
        var app = SteamKeyValues.Parse("appinfo { config { launch { 0 { executable game.exe } 1 { executable other.exe config { oslist linux } } 2 { executable guessed.exe } 3 { executable \"../escape.exe\" } } } }");
        Assert.Equal(new[] { "game.exe" }, SteamGameMetadata.ResolveProcesses(app, install));
        Assert.Throws<InvalidDataException>(() => SteamGameMetadata.InstallationPath(fixture.Root, "../outside"));
        Assert.Throws<InvalidDataException>(() => SteamGameMetadata.InstallationPath(fixture.Root, @"C:\outside"));
    }
    [Fact]
    public async Task ManifestScanSupportsMultipleLibrariesCachesResultsAndExplicitRefreshRebuilds()
    {
        using var fixture = new Fixture(); var second = Path.Combine(fixture.Root, "AnotherLibrary");
        Directory.CreateDirectory(Path.Combine(second, "steamapps"));
        File.WriteAllText(Path.Combine(fixture.Root, "steamapps", "libraryfolders.vdf"), "libraryfolders { 0 { path \"" + fixture.Root.Replace("\\", "\\\\") + "\" } 1 { path \"" + second.Replace("\\", "\\\\") + "\" } }");
        WriteGame(fixture.Root, 1, "First"); WriteGame(second, 2, "Second");
        // A manifest nested inside common must never be discovered.
        WriteGame(Path.Combine(fixture.Root, "steamapps", "common", "Unrelated"), 3, "Hidden");
        using var catalog = new SteamCatalog(() => fixture.Root);
        var first = await catalog.ReadAsync(false, 0, null, CancellationToken.None);
        Assert.False(first.FromCache); Assert.Equal(2, first.TotalCount); Assert.All(first.Games, x => Assert.Empty(x.ProcessNames));
        var cached = await catalog.ReadAsync(false, 0, null, CancellationToken.None);
        Assert.True(cached.FromCache); Assert.Equal(first.ScanId, cached.ScanId);
        var fresh = await catalog.ReadAsync(true, 0, null, CancellationToken.None);
        Assert.False(fresh.FromCache); Assert.NotEqual(first.ScanId, fresh.ScanId);
    }
    [Fact]
    public async Task FileNotificationInvalidatesCacheWithoutPeriodicScanning()
    {
        using var fixture = new Fixture(); WriteGame(fixture.Root, 1, "First");
        using var catalog = new SteamCatalog(() => fixture.Root);
        var first = await catalog.ReadAsync(false, 0, null, CancellationToken.None);
        WriteGame(fixture.Root, 2, "Second");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Test-only bounded retry waits for asynchronous OS file notification; production has no timer.
        SteamCatalogPageProxy? result = null;
        while (!timeout.IsCancellationRequested)
        {
            var next = await catalog.ReadAsync(false, 0, null, timeout.Token);
            if (next.TotalCount == 2) { result = new(next.ScanId); break; }
            await Task.Delay(25, timeout.Token);
        }
        Assert.NotNull(result); Assert.NotEqual(first.ScanId, result.ScanId);
    }
    private sealed record SteamCatalogPageProxy(string ScanId);
    [Fact]
    public async Task PagedResultsDoNotRescanMidReadAndRejectStaleGeneration()
    {
        using var fixture = new Fixture();
        for (uint id = 1; id <= 42; id++) WriteGame(fixture.Root, id, "Game" + id);
        using var catalog = new SteamCatalog(() => fixture.Root);
        var first = await catalog.ReadAsync(false, 0, null, CancellationToken.None);
        Assert.Equal(40, first.Games.Length); Assert.Equal(40, first.NextOffset);
        var second = await catalog.ReadAsync(false, 40, first.ScanId, CancellationToken.None);
        Assert.Equal(2, second.Games.Length); Assert.Null(second.NextOffset); Assert.Equal(first.ScanId, second.ScanId);
        await catalog.ReadAsync(true, 0, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ReadAsync(false, 40, first.ScanId, CancellationToken.None));
    }
    private static void WriteGame(string library, uint id, string name)
    {
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", name));
        File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"), $"AppState {{ appid {id} name {name} installdir {name} SizeOnDisk 123 }}");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "axiom-steam-" + Guid.NewGuid());
        public Fixture() { Directory.CreateDirectory(Path.Combine(Root, "steamapps")); Directory.CreateDirectory(Path.Combine(Root, "appcache")); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static MemoryStream AppInfo(uint magic)
    {
        var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        var names = new[] { "appinfo", "config", "launch", "0", "executable", "oslist" };
        writer.Write(magic); writer.Write(1u); if (magic == 0x07564429) writer.Write(0L);
        writer.Write(730u); var sizeAt = stream.Position; writer.Write(0u); var start = stream.Position;
        writer.Write(new byte[magic == 0x07564427 ? 40 : 60]);
        void Key(byte type, string key) { writer.Write(type); if (magic == 0x07564429) writer.Write(Array.IndexOf(names, key)); else { writer.Write(Encoding.UTF8.GetBytes(key)); writer.Write((byte)0); } }
        void Value(string key, string text) { Key(1, key); writer.Write(Encoding.UTF8.GetBytes(text)); writer.Write((byte)0); }
        Key(0, "appinfo"); Key(0, "config"); Key(0, "launch"); Key(0, "0"); Value("executable", @"bin\win64\cs2.exe");
        Key(0, "config"); Value("oslist", "windows");
        for (var i = 0; i < 6; i++) writer.Write((byte)8);
        var end = stream.Position; stream.Position = sizeAt; writer.Write((uint)(end - start)); stream.Position = end;
        writer.Write(0u);
        if (magic == 0x07564429)
        {
            var strings = stream.Position; writer.Write((uint)names.Length);
            foreach (var name in names) { writer.Write(Encoding.UTF8.GetBytes(name)); writer.Write((byte)0); }
            stream.Position = 8; writer.Write(strings);
        }
        stream.Position = 0; return stream;
    }
}
