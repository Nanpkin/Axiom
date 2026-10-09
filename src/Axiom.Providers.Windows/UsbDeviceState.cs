namespace Axiom.Providers.Windows;

public sealed record UsbDeviceValue(string InstanceId, bool? Present);

public sealed class UsbDeviceState(string[] selected)
{
    private HashSet<string>? present;
    public void Replace(string[]? instanceIds)
    {
        if (instanceIds is null) { present = null; return; }
        if (instanceIds.Length > 256 || instanceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 255 || id.Any(char.IsControl)))
            throw new IOException("USB 设备查询超出容量或返回无效身份。");
        present = instanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    // Publish positives before negatives so switching between selected devices does not create a false all-absent edge.
    public UsbDeviceValue[] Samples() => [.. selected.Select(id => new UsbDeviceValue(id, present is null ? null : present.Contains(id)))
        .OrderByDescending(v => v.Present == true)];
}
