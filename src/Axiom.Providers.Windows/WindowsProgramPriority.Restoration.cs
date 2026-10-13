namespace Axiom.Providers.Windows;

public static partial class WindowsProgramPriority
{
    public static async Task<IRestorableSettingSession> CreateRestorableSessionAsync(string path, CancellationToken token)
    {
        PrioritySession? session = null;
        (bool Success, string Reason) result;
        try
        {
            result = await Commands.RunAsync(cancellation =>
            {
                var candidates = Capture([path], cancellation); var transferred = false;
                try
                {
                    session = new(candidates, path);
                    transferred = true; cancellation.ThrowIfCancellationRequested();
                    return (true, "已固定本轮程序实例并读取原优先级。");
                }
                finally { if (!transferred) foreach (var candidate in candidates) candidate.Dispose(); }
            }, token);
        }
        catch
        {
            await Commands.DrainAsync(); if (session is not null) await session.DisposeAsync(); throw;
        }
        if (result.Success && session is not null) return session;
        await Commands.DrainAsync(); if (session is not null) await session.DisposeAsync();
        throw new IOException(result.Reason);
    }
    private sealed class PrioritySession : SerializedRestorableSettingSession
    {
        private readonly List<Candidate> candidates;
        private readonly string path;
        private readonly List<ConditionalSettingTransaction<uint>> settings = [];
        private CancellationToken currentToken;
        public PrioritySession(List<Candidate> candidates, string path) : base(Commands)
        {
            this.candidates = candidates; this.path = path;
            foreach (var candidate in candidates)
            {
                var identity = candidate.Identify(out var actual);
                if (identity == PriorityProcessIdentity.Unknown) throw new IOException("程序身份未知，未修改优先级。");
                if (identity != PriorityProcessIdentity.Owned || !string.Equals(actual, path, StringComparison.OrdinalIgnoreCase)) continue;
                uint Read()
                {
                    if (candidate.Identify(out var latest) != PriorityProcessIdentity.Owned || !string.Equals(latest, path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("本轮进程身份已变化。");
                    var value = candidate.GetPriority();
                    if (value is not (0x20 or 0x40 or 0x80 or 0x100 or 0x4000 or 0x8000)) throw new IOException("优先级基线未知。");
                    return value;
                }
                settings.Add(new(Read(), Read, value => candidate.SetPriority(value, currentToken), "程序实例优先级"));
            }
            if (settings.Count == 0) throw new IOException("本轮没有可核验的匹配运行实例，未修改优先级。");
        }
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            if (!string.Equals(Path.GetFullPath(parameters["executable"].Trim()), path, StringComparison.OrdinalIgnoreCase))
            { RelinquishCore(); return new(false, "本轮程序目标变化，未操作替代实例。", false); }
            currentToken = token; var value = ProgramPriorityOperation.Class(parameters["priority"]);
            return Aggregate(settings.Select(s => s.Apply(value, token)).ToArray(), "应用");
        }
        protected override RestorableSettingResult ReleaseCore(CancellationToken token)
        { currentToken = token; return Aggregate(settings.Select(s => s.Release(token)).ToArray(), "释放"); }
        private static RestorableSettingResult Aggregate(RestorableSettingResult[] receipts, string phase) => new(receipts.All(r => r.Success),
            $"本轮{receipts.Length}个固定程序实例{phase}：" + string.Join("；", receipts.Select(r => r.Reason).Distinct().Take(3)), receipts.Any(r => r.Owned));
        protected override void RelinquishCore() { foreach (var setting in settings) setting.Relinquish(); }
        protected override void DisposeCore() { foreach (var candidate in candidates) candidate.Dispose(); }
    }
}
