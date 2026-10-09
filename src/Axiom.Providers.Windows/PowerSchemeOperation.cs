namespace Axiom.Providers.Windows;

internal interface IPowerSchemeApi
{
    uint Enumerate(uint index, out Guid scheme);
    uint SetActive(Guid scheme);
    uint GetActive(out Guid scheme);
}

internal static class PowerSchemeOperation
{
    private const uint NoMoreItems = 259;
    internal static (bool Success, string Reason) Apply(IPowerSchemeApi api, Guid scheme, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var found = false;
        for (uint index = 0; index < 4096; index++)
        {
            token.ThrowIfCancellationRequested();
            var error = api.Enumerate(index, out var candidate);
            token.ThrowIfCancellationRequested();
            if (error == NoMoreItems) break;
            if (error != 0) return (false, "无法确认本机电源方案，本次未切换；请检查系统电源设置与权限。");
            if (candidate == scheme) { found = true; break; }
            if (index == 4095) return (false, "本机电源方案数量超出可安全处理范围，本次未切换。");
        }
        if (!found) return (false, $"本机没有标准{name}电源方案，本次未切换；Modern Standby或厂商设备可能仅提供平衡方案。不会创建或导入方案。");
        token.ThrowIfCancellationRequested();
        if (api.SetActive(scheme) != 0) return (false, $"Windows拒绝切换到{name}电源方案；请检查权限或系统策略。不自动重试。");
        token.ThrowIfCancellationRequested();
        var readError = api.GetActive(out var actual);
        token.ThrowIfCancellationRequested();
        if (readError != 0) return (false, "电源方案切换请求已发出，但当前方案无法读回，结果未确认；不重试、不恢复旧方案。");
        return actual == scheme
            ? (true, $"Windows当前电源方案已读回确认为{name}；新方案将保留，条件结束或暂停不恢复。")
            : (false, "电源方案切换后读回不一致，可能被系统或其他操作改变；不重试、不恢复旧方案。");
    }
}
