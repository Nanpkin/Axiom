using System.Text;
using Axiom.Contracts;

namespace Axiom.Infrastructure;

// Local Valve KeyValues only. These parsers do not enumerate game directories or execute launch commands.
public sealed class SteamKeyValues
{
    public string? Value { get; init; }
    public List<KeyValuePair<string, SteamKeyValues>> Children { get; } = [];
    public SteamKeyValues? Get(string key) => Children.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    public string? Text(string key) => Get(key)?.Value;
    public static SteamKeyValues Parse(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Steam 元数据过大。");
        var index = 0; var nodes = 0; var quoted = false;
        string? ReadToken()
        {
            quoted = false;
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]) || text[index] == '\uFEFF') { index++; continue; }
                if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
                { while (index < text.Length && text[index] != '\n') index++; continue; }
                break;
            }
            if (index == text.Length) return null;
            var ch = text[index++];
            if (ch is '{' or '}') return ch.ToString();
            var result = new StringBuilder();
            if (ch == '"')
            {
                quoted = true;
                while (index < text.Length)
                {
                    ch = text[index++]; if (ch == '"') return result.ToString();
                    if (ch == '\\' && index < text.Length && text[index] is '\\' or '"') ch = text[index++];
                    result.Append(ch);
                }
                throw new InvalidDataException("Steam 元数据字符串未结束。");
            }
            result.Append(ch);
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}')) result.Append(text[index++]);
            return result.ToString();
        }
        SteamKeyValues ReadObject(int depth, bool nested)
        {
            if (depth > 32) throw new InvalidDataException("Steam 元数据嵌套过深。");
            var result = new SteamKeyValues();
            while (true)
            {
                var key = ReadToken();
                if (key is null) { if (nested) throw new InvalidDataException("Steam 元数据对象未结束。"); return result; }
                if (key == "}" && !quoted) { if (!nested) throw new InvalidDataException("Steam 元数据对象格式无效。"); return result; }
                if (key == "{" && !quoted || ++nodes > 100000) throw new InvalidDataException("Steam 元数据格式无效。");
                var value = ReadToken() ?? throw new InvalidDataException("Steam 元数据缺少值。");
                if (value == "}" && !quoted) throw new InvalidDataException("Steam 元数据缺少值。");
                result.Children.Add(new(key, value == "{" && !quoted ? ReadObject(depth + 1, true) : new() { Value = value }));
            }
        }
        return ReadObject(0, false);
    }
}

public static class SteamAppInfo
{
    // Format reference: https://github.com/xPaw/SteamAppInfo (v39-v41). No external parser dependency.
    public static Dictionary<uint, SteamKeyValues> Read(Stream stream, IReadOnlySet<uint> installed, CancellationToken token)
    {
        if (!stream.CanSeek || stream.Length > 512L * 1024 * 1024) throw new InvalidDataException("Steam 启动缓存格式或大小不受支持。");
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var magic = reader.ReadUInt32(); reader.ReadUInt32();
        if (magic is not (0x07564427 or 0x07564428 or 0x07564429)) throw new InvalidDataException("Steam 启动缓存版本暂不支持，可手动选择程序。");
        string[]? names = null; var entriesEnd = stream.Length;
        if (magic == 0x07564429)
        {
            var offset = reader.ReadInt64();
            if (offset < 16 || offset > stream.Length - 4) throw new InvalidDataException("Steam 启动缓存索引无效。");
            stream.Position = offset; entriesEnd = offset;
            var count = reader.ReadUInt32();
            if (count > 200000) throw new InvalidDataException("Steam 启动缓存索引过大。");
            names = new string[count]; var textBytes = 0;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested(); names[i] = ReadString(reader, stream.Length, 4096);
                textBytes += names[i].Length;
                if (textBytes > 16 * 1024 * 1024) throw new InvalidDataException("Steam 启动缓存索引过大。");
            }
            stream.Position = 16;
        }
        var results = new Dictionary<uint, SteamKeyValues>();
        while (stream.Position + 4 <= entriesEnd)
        {
            token.ThrowIfCancellationRequested(); var appId = reader.ReadUInt32(); if (appId == 0) break;
            var size = reader.ReadUInt32(); var end = checked(stream.Position + size);
            var headerSize = magic == 0x07564427 ? 40 : 60;
            if (size < headerSize || end > entriesEnd) throw new InvalidDataException("Steam 启动缓存条目无效。");
            if (installed.Contains(appId))
            {
                if (size > 8 * 1024 * 1024) throw new InvalidDataException("Steam 启动缓存条目过大。");
                stream.Position += headerSize; var nodes = 0;
                results[appId] = ReadObject(reader, names, end, 0, ref nodes, token);
            }
            stream.Position = end;
        }
        return results;
    }
    private static SteamKeyValues ReadObject(BinaryReader reader, string[]? names, long end, int depth, ref int nodes, CancellationToken token)
    {
        if (depth > 64) throw new InvalidDataException("Steam 启动缓存嵌套过深。");
        var result = new SteamKeyValues();
        while (reader.BaseStream.Position < end)
        {
            token.ThrowIfCancellationRequested(); var type = reader.ReadByte(); if (type == 8) return result;
            if (++nodes > 100000) throw new InvalidDataException("Steam 启动缓存内容过多。");
            string key;
            if (names is null) key = ReadString(reader, end, 4096);
            else
            {
                Require(reader, end, 4); var id = reader.ReadUInt32();
                key = id < names.Length ? names[id] : throw new InvalidDataException("Steam 启动缓存键无效。");
            }
            SteamKeyValues value;
            if (type == 0) value = ReadObject(reader, names, end, depth + 1, ref nodes, token);
            else if (type == 1) value = new() { Value = ReadString(reader, end, 16384) };
            else
            {
                var bytes = type switch { 2 or 3 or 4 or 6 => 4, 7 or 10 => 8, _ => throw new InvalidDataException("Steam 启动缓存值类型暂不支持。") };
                Require(reader, end, bytes); reader.BaseStream.Position += bytes; value = new();
            }
            result.Children.Add(new(key, value));
        }
        throw new InvalidDataException("Steam 启动缓存对象未结束。");
    }
    private static void Require(BinaryReader reader, long end, int count)
    { if (reader.BaseStream.Position + count > end) throw new InvalidDataException("Steam 启动缓存被截断。"); }
    private static string ReadString(BinaryReader reader, long end, int maximum)
    {
        var bytes = new List<byte>();
        while (reader.BaseStream.Position < end && bytes.Count <= maximum)
        { var next = reader.ReadByte(); if (next == 0) return Encoding.UTF8.GetString([.. bytes]); bytes.Add(next); }
        throw new InvalidDataException("Steam 启动缓存字符串无效。");
    }
}

public static class SteamGameMetadata
{
    public static string InstallationPath(string library, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Contains(':') || Path.IsPathRooted(directory))
            throw new InvalidDataException("Steam 安装目录无效。");
        var common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
        var result = Path.GetFullPath(Path.Combine(common, directory));
        if (!result.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam 安装目录超出游戏库。");
        return result;
    }
    public static string[] ResolveProcesses(SteamKeyValues app, string installPath)
    {
        var config = (app.Get("appinfo") ?? app).Get("config");
        var launches = config?.Get("launch"); if (launches is null) return [];
        var names = new List<string>();
        foreach (var launch in launches.Children.Select(x => x.Value))
        {
            var os = launch.Get("config")?.Text("oslist");
            if (os is not null && !os.Split(',').Any(x => x.Trim().Equals("windows", StringComparison.OrdinalIgnoreCase))) continue;
            var executable = launch.Text("executable");
            if (string.IsNullOrWhiteSpace(executable) || executable.Length > 1024 || Path.IsPathRooted(executable) || executable.Contains(':')) continue;
            executable = executable.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var working = (launch.Text("workingdir") ?? "").Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (working.Contains(':') || Path.IsPathRooted(working)) continue;
            foreach (var relative in new[] { executable, Path.Combine(working, executable) }.Distinct())
            {
                var full = Path.GetFullPath(Path.Combine(installPath, relative));
                if (!full.StartsWith(Path.GetFullPath(installPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(full);
                if (ProcessGroups.ValidProcessName(name) && File.Exists(full)) names.Add(name.ToLowerInvariant());
            }
        }
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).Take(16)];
    }
}
