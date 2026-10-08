using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

/// <summary>Read-only display discovery. All writes belong to a separately authorized capability.</summary>
public static class WindowsHdrDisplayProbe
{
    private const uint OnlyActivePaths = 2;
    private const int AdvancedColorInfo2 = 15;

    public static IReadOnlyList<HdrDisplaySnapshot> ReadActiveDisplays()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount);
            if (error != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {error}");
            if (pathCount > 256 || modeCount > 512) throw new InvalidOperationException("Display topology exceeds probe capacity.");

            var paths = new DisplayPath[Math.Max(1, (int)pathCount)];
            // QueryDisplayConfig fills the mode table, even though this probe only needs target IDs.
            var modes = Marshal.AllocHGlobal(Math.Max(1, (int)modeCount) * 128);
            try
            {
                error = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == 122) continue; // Topology changed between sizing and query.
                if (error != 0) throw new InvalidOperationException($"QueryDisplayConfig failed: {error}");

                var result = new List<HdrDisplaySnapshot>();
                for (var i = 0; i < pathCount; i++)
                {
                    if ((paths[i].Flags & 1) == 0) continue;
                    var target = paths[i].Target;
                    var info = new HdrInfo2
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = AdvancedColorInfo2,
                            Size = (uint)Marshal.SizeOf<HdrInfo2>(),
                            Adapter = target.Adapter,
                            Id = target.Id
                        }
                    };
                    var infoError = DisplayConfigGetDeviceInfo(ref info);
                    var id = $"{target.Adapter.HighPart:X8}{target.Adapter.LowPart:X8}/{target.Id}";
                    var name = new TargetNamePacket
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = 2,
                            Size = (uint)Marshal.SizeOf<TargetNamePacket>(),
                            Adapter = target.Adapter,
                            Id = target.Id
                        },
                        FriendlyName = string.Empty,
                        DevicePath = string.Empty
                    };
                    var nameError = DisplayConfigGetDeviceInfo(ref name);
                    var friendly = nameError == 0 ? name.FriendlyName.TrimEnd('\0') : id;
                    var devicePath = nameError == 0 ? name.DevicePath.TrimEnd('\0') : string.Empty;
                    result.Add(infoError == 0
                        ? new HdrDisplaySnapshot(id, friendly, devicePath, (info.Flags & (1u << 4)) != 0,
                            (info.Flags & (1u << 5)) != 0, info.ActiveColorMode == 2, null)
                        : new HdrDisplaySnapshot(id, friendly, devicePath, null, null, null,
                            $"DisplayConfigGetDeviceInfo(HDR) failed: {infoError}"));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("Display topology changed repeatedly during probe.");
    }

    /// <summary>Applies an absolute HDR value to one previously discovered monitor and reads it back.</summary>
    public static HdrApplyResult Apply(string devicePath, bool enabled, bool expectedEnabled)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return new(false, null, "A monitor device path is required.");
        var matches = ReadActiveDisplays().Where(display =>
            string.Equals(display.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return new(false, null, "Selected monitor is disconnected or ambiguous.");
        var before = matches[0];
        if (before.Supported != true || before.UserEnabled is null || before.Active is null)
            return new(false, before, before.Error ?? "HDR is unavailable on this monitor.");
        if (before.UserEnabled != expectedEnabled || before.Active != expectedEnabled)
            return new(false, before, "HDR changed outside Axiom before this action; no setting was applied.");
        if (before.UserEnabled == enabled && before.Active == enabled) return new(true, before, "Already at target.");
        if (enabled && before.UserEnabled == true && before.Active == false)
            return new(false, before, "HDR is enabled but inactive under the current display policy.");

        var idParts = before.Id.Split('/');
        if (idParts.Length != 2 || !ulong.TryParse(idParts[0], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var adapter) ||
            !uint.TryParse(idParts[1], out var targetId))
            return new(false, before, "Selected monitor identifier is invalid.");

        var packet = new SetHdrPacket
        {
            Header = new DeviceInfoHeader
            {
                Type = 16,
                Size = (uint)Marshal.SizeOf<SetHdrPacket>(),
                Adapter = new Luid { LowPart = (uint)adapter, HighPart = (int)(adapter >> 32) },
                Id = targetId
            },
            Enabled = enabled ? 1u : 0u
        };
        var error = DisplayConfigSetDeviceInfo(ref packet);
        if (error != 0) return new(false, before, $"DisplayConfigSetDeviceInfo(HDR) failed: {error}");

        var after = ReadActiveDisplays().SingleOrDefault(display =>
            string.Equals(display.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
        if (after is null) return new(false, null, "Selected monitor disappeared after applying HDR.");
        return after.UserEnabled == enabled && after.Active == enabled
            ? new(true, after, "HDR state confirmed.")
            : new(false, after, "HDR request returned but the monitor did not reach the target state.");
    }

    public sealed record HdrDisplaySnapshot(string Id, string FriendlyName, string DevicePath,
        bool? Supported, bool? UserEnabled,
        bool? Active, string? Error);

    public sealed record HdrApplyResult(bool Confirmed, HdrDisplaySnapshot? Observed, string Reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo
    {
        public Luid Adapter;
        public uint Id;
        public uint ModeIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid Adapter;
        public uint Id;
        public uint ModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        public SourceInfo Source;
        public TargetInfo Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HdrInfo2
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetNamePacket
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCode;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetHdrPacket
    {
        public DeviceInfoHeader Header;
        public uint Enabled;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, IntPtr modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref HdrInfo2 packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetNamePacket packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetHdrPacket packet);
}
