using Axiom.Domain;

namespace Axiom.Contracts;

public static class NetworkCategoryCapabilities
{
    public const string State = "network.category-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "网络", "Windows网络分类",
        "任一已连接网络被Windows归为所选分类时成立。",
        [new("category", "网络分类", TaskFieldKind.Choice, true, "只选择Windows分类，不选择网络名称；专用或域已验证不证明安全可信。", "public", 7,
            [new("public", "公用"), new("private", "专用"), new("domain", "域已验证")])],
        "按需共享Windows分类及连接变化通知，无轮询或联网探测。首次、重新启用或未知恢复只建立命令基线；已知持续条件可保持唤醒。暂停、删除或断开后释放订阅。",
        "任一已知匹配可成立；无匹配但仍有无法查询的网络时保持未知，不冒充非匹配。明确无连接时三个条件均不成立。分类不是Internet可用性、位置或安全身份，不读取或保存网络名称、SSID、地址或网络ID。", IsStateCondition: true);
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("category", out var category) || !ValidResource(category))
            throw new ArgumentException("请选择Windows网络的公用、专用或域已验证分类。");
        return category;
    }
    public static bool ValidResource(string resource) => resource is "public" or "private" or "domain";
}
