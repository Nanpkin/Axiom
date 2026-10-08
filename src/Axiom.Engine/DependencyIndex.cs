using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

internal sealed class DependencyIndex
{
    private readonly Dictionary<string, HashSet<ResourceKey>> byRule = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceKey, SortedSet<string>> byKey = [];

    internal void Set(string ruleId, IEnumerable<ResourceKey> dependencies)
    {
        Contract.Text(ruleId, nameof(ruleId));
        ArgumentNullException.ThrowIfNull(dependencies);
        var input = dependencies.Take(1025).ToArray();
        if (input.Length > 1024) throw new ArgumentException("Dependency input exceeds capacity.", nameof(dependencies));
        var keys = input.ToHashSet();
        if (keys.Count > 1024 || keys.Contains(null!)) throw new ArgumentException("Invalid dependencies.", nameof(dependencies));
        if (!byRule.ContainsKey(ruleId) && byRule.Count >= 100_000) throw new InvalidOperationException("Rule capacity exceeded.");
        Remove(ruleId);
        byRule[ruleId] = keys;
        foreach (var key in keys)
        {
            if (!byKey.TryGetValue(key, out var rules)) byKey[key] = rules = new(StringComparer.Ordinal);
            rules.Add(ruleId);
        }
    }
    internal void Remove(string ruleId)
    {
        if (!byRule.Remove(ruleId, out var keys)) return;
        foreach (var key in keys)
        {
            var rules = byKey[key];
            rules.Remove(ruleId);
            if (rules.Count == 0) byKey.Remove(key);
        }
    }
    internal ImmutableArray<string> Match(ResourceKey key) => byKey.TryGetValue(key, out var rules) ? [.. rules] : [];
    internal void Clear() { byRule.Clear(); byKey.Clear(); }
}
