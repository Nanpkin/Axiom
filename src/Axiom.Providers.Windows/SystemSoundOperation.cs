namespace Axiom.Providers.Windows;

internal interface ISystemSoundApi { bool Request(uint soundType); }

internal static class SystemSoundOperation
{
    // MessageBeep: MB_OK, MB_ICONASTERISK, MB_ICONEXCLAMATION, MB_ICONHAND.
    internal static uint Type(string sound) => sound switch
    { "default" => 0, "info" => 0x40, "warning" => 0x30, "error" => 0x10, _ => throw new ArgumentException("不支持此系统声音。") };

    internal static (bool Success, string Reason) Apply(string sound, ISystemSoundApi api, CancellationToken token)
    {
        var type = Type(sound); token.ThrowIfCancellationRequested();
        bool accepted;
        try { accepted = api.Request(type); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { return (false, "系统提示音请求未确认受理；Windows或权限可能不可用，不自动重试。"); }
        token.ThrowIfCancellationRequested();
        return accepted
            ? (true, "Windows已接受一次系统提示音请求，不保证听见；静音、声音主题、远程会话或设备可能导致无声。")
            : (false, "Windows未接受系统提示音请求；不自动重试。");
    }
}
