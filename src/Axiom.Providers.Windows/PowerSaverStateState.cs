namespace Axiom.Providers.Windows;

public static class PowerSaverStateState
{
    public static PowerContextValue[] Samples(string[] resources, byte? status) =>
        [.. resources.Select(id => new PowerContextValue(id, Evaluate(id, status)))];
    public static bool? Evaluate(string resource, byte? status) => status is not (0 or 1) ? null : resource switch
    {
        "on" => status == 1,
        "off" => status == 0,
        _ => throw new ArgumentException("无效Windows节能状态条件。")
    };
}
