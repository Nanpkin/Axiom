namespace Axiom.Providers.Windows;

public static partial class WindowsPowerScheme
{
    public static async Task<IRestorableSettingSession> CreateRestorableSessionAsync(CancellationToken token)
    {
        SchemeSession? session = null;
        (bool Success, string Reason) result;
        try { result = await Commands.RunAsync(cancellation =>
        {
            var baseline = ReadActive(cancellation);
            cancellation.ThrowIfCancellationRequested();
            session = new(baseline);
            return (true, "已读取本轮原电源方案。");
        }, token); }
        catch
        {
            await Commands.DrainAsync();
            if (session is not null) await session.DisposeAsync();
            throw;
        }
        if (result.Success && session is not null) return session;
        await Commands.DrainAsync();
        if (session is not null) await session.DisposeAsync();
        throw new IOException(result.Reason);
    }
    private sealed class SchemeSession : SerializedRestorableSettingSession
    {
        private readonly ConditionalSettingTransaction<Guid> setting;
        private CancellationToken currentToken;
        public SchemeSession(Guid baseline) : base(Commands) => setting = new(baseline,
            () => ReadActive(CancellationToken.None), Write, "电源方案");
        private void Write(Guid value)
        {
            var api = new Api(); var found = false;
            for (uint index = 0; index < 4096; index++)
            {
                currentToken.ThrowIfCancellationRequested();
                var error = api.Enumerate(index, out var candidate);
                if (error == 259) break;
                if (error != 0) throw new IOException("无法核验电源方案。");
                if (candidate == value) { found = true; break; }
            }
            currentToken.ThrowIfCancellationRequested();
            if (!found || api.SetActive(value) != 0) throw new IOException("原方案缺失或Windows拒绝切换。");
        }
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            currentToken = token;
            var target = parameters["scheme"] switch
            {
                "balanced" => new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
                "saver" => new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"),
                "performance" => new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
                _ => throw new ArgumentException("只能应用已有标准电源方案。")
            };
            return setting.Apply(target, token);
        }
        protected override RestorableSettingResult ReleaseCore(CancellationToken token) { currentToken = token; return setting.Release(token); }
        protected override void RelinquishCore() => setting.Relinquish();
        protected override void DisposeCore() { }
    }
}
