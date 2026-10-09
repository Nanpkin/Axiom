namespace Axiom.Providers.Windows;

public static class PowerSchemeStateState
{
    public static PowerContextValue[] Samples(string[] resources, Guid? scheme) =>
        [.. resources.Select(id => new PowerContextValue(id, Evaluate(id, scheme)))];
    public static bool? Evaluate(string resource, Guid? scheme) => !scheme.HasValue || scheme.Value == Guid.Empty ? null : resource switch
    {
        "balanced" => scheme == new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
        "saver" => scheme == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"),
        "performance" => scheme == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        _ => throw new ArgumentException("无效标准电源方案条件。")
    };
}
