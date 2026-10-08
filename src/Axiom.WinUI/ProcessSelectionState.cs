using Axiom.Contracts;

namespace Axiom.WinUI;

internal sealed class ProcessSelectionState(string? value, bool executablePaths = false)
{
    private readonly List<string> names = [.. ProcessGroups.SplitNames(value)];
    public IReadOnlyList<string> Names => names;
    public string Value => string.Join("\n", names);
    public bool Contains(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
    public void Set(IEnumerable<string> entries, bool selected)
    {
        var incoming = (executablePaths ? entries.Select(ExecutablePaths.ValidatePath) : entries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!executablePaths && incoming.Any(x => !ProcessGroups.ValidProcessName(x))) throw new ArgumentException("请填写 .exe 文件名，例如 yuanshen.exe；不要填写路径或启动参数。");
        if (selected)
        {
            var additions = incoming.Where(x => !Contains(x)).ToArray();
            if (names.Count + additions.Length > (executablePaths ? ExecutablePaths.MaximumEntries : ProcessGroups.MaximumEntries))
                throw new ArgumentException(executablePaths ? "每个动作最多选择 32 个目标程序。" : "每个任务或组最多选择 128 个程序。");
            if (executablePaths && string.Join("\n", names.Concat(additions)).Length > ExecutablePaths.MaximumTextLength) throw new ArgumentException("程序路径名单过长。");
            names.AddRange(executablePaths ? additions : additions.Select(x => x.ToLowerInvariant()));
        }
        else names.RemoveAll(x => incoming.Contains(x, StringComparer.OrdinalIgnoreCase));
    }
}
