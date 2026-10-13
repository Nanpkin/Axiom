using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class RestorableSettingInvocations
{
    public static ValueSchema Schema { get; } = new("axiom.restorable-setting", 1);
    public static bool IsRestorable(string capabilityId) => capabilityId.EndsWith(".restore", StringComparison.Ordinal);
    public static void ValidateLease(TaskInvocation invocation)
    {
        if (!IsRestorable(invocation.CapabilityId) || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count is < 1 or > 32 ||
            invocation.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value is null || p.Value.Length > 32768) ||
            !int.TryParse(invocation.Parameters.GetValueOrDefault("delaySeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var delay) || delay is < 0 or > 86400)
            throw new ArgumentException("条件恢复租约配置无效。");
    }
    public static TaskInvocation Normalize(TaskInvocation invocation)
    {
        ValidateLease(invocation);
        var parameters = invocation.Parameters.SetItem("delaySeconds", "0");
        if (invocation.CapabilityId == "audio.playback.restore" && parameters.GetValueOrDefault("operation") != "volume") parameters = parameters.SetItem("percent", "0");
        return invocation with { Parameters = parameters };
    }
    public static string Encode(TaskInvocation invocation)
    {
        var normalized = Normalize(invocation);
        return JsonSerializer.Serialize(new { normalized.CapabilityId, normalized.SchemaVersion,
            Parameters = new SortedDictionary<string, string>(normalized.Parameters, StringComparer.Ordinal) });
    }
    public static TaskInvocation Decode(string text) => JsonSerializer.Deserialize<TaskInvocation>(text) ?? throw new ArgumentException("条件恢复参数为空。");
}
