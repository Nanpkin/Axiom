using System.Text.Json;

namespace Axiom.WinUI;

// UI organization only: task identifiers remain owned by the host.
internal sealed record RuleCollection(string Id, string Name);
internal sealed record RuleCollections(RuleCollection[] Collections, Dictionary<string, string> Assignments, string Selected = "")
{
    public const string Unfiled = "unfiled";
    public static RuleCollections Empty => new([], []);
    public string CollectionOf(string taskId) => Assignments.TryGetValue(taskId, out var id) && Collections.Any(x => x.Id == id) ? id : Unfiled;
    public bool Includes(string taskId) => Selected.Length == 0 || CollectionOf(taskId) == Selected;

    public RuleCollections Rename(string? id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24 || name.Any(char.IsControl)) throw new ArgumentException("分类名称需要 1–24 个字符。");
        if (name is "全部" or "未分类" || Collections.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("这个分类名称已经存在，请换一个名称。");
        if (id is null)
        {
            if (Collections.Length >= 32) throw new ArgumentException("最多创建 32 个分类。");
            var created = new RuleCollection(Guid.NewGuid().ToString("N"), name);
            return this with { Collections = [.. Collections, created], Selected = created.Id };
        }
        if (!Collections.Any(x => x.Id == id)) throw new ArgumentException("分类已不存在。");
        return this with { Collections = Collections.Select(x => x.Id == id ? x with { Name = name } : x).ToArray() };
    }

    public RuleCollections Remove(string id) => this with
    {
        Collections = Collections.Where(x => x.Id != id).ToArray(),
        Assignments = Assignments.Where(x => x.Value != id).ToDictionary(),
        Selected = Selected == id ? "" : Selected
    };

    public RuleCollections Move(string taskId, string id)
    {
        if (id != Unfiled && !Collections.Any(x => x.Id == id)) throw new ArgumentException("分类已不存在。");
        var assignments = new Dictionary<string, string>(Assignments);
        if (id == Unfiled) assignments.Remove(taskId); else assignments[taskId] = id;
        return this with { Assignments = assignments };
    }

    public static RuleCollections Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var value = JsonSerializer.Deserialize<RuleCollections>(File.ReadAllText(path)) ?? throw new IOException("分类文件为空。");
        if (value.Collections is null || value.Assignments is null || value.Selected is null || value.Collections.Length > 32 ||
            value.Collections.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || x.Id == Unfiled || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 24) ||
            value.Collections.Select(x => x.Id).Distinct().Count() != value.Collections.Length)
            throw new IOException("分类文件格式无效。");
        return value.Selected.Length == 0 || value.Selected == Unfiled || value.Collections.Any(x => x.Id == value.Selected)
            ? value : value with { Selected = "" };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
