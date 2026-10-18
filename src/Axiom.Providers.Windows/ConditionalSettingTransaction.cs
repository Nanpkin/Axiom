namespace Axiom.Providers.Windows;

// A single captured setting. The caller serializes native access and pins its actual target.
internal sealed class ConditionalSettingTransaction<T>(T baseline, Func<T> read, Action<T> write, string name,
    IEqualityComparer<T>? comparer = null, Func<T, T, bool>? acceptsTarget = null) where T : struct
{
    private readonly IEqualityComparer<T> equality = comparer ?? EqualityComparer<T>.Default;
    private readonly T original = baseline;
    private T confirmed = baseline;
    private int relinquished;
    private bool released;
    private bool fadePending;
    private T pendingTarget;
    public bool Owned => Volatile.Read(ref relinquished) == 0 && !released;
    internal T Confirmed => confirmed;
    internal bool HasPendingFadeWrite => fadePending;
    internal bool UnconfirmedFailure { get; private set; }
    public void Relinquish()
    {
        if (fadePending) UnconfirmedFailure = true;
        Interlocked.Exchange(ref relinquished, 1);
    }
    internal bool TryCompleteConfirmedRelease()
    {
        if (!Owned || fadePending || !equality.Equals(confirmed, original)) return false;
        released = true; Relinquish(); return true;
    }
    public RestorableSettingResult Apply(T target, CancellationToken token)
    {
        if (!Owned) return new(false, name + "本次控制已让出；不会重新抓取基线或重试。", false);
        if (fadePending) { Relinquish(); return new(false, name + "上次渐变写入尚未确认，本次让出，不覆盖。", false); }
        try
        {
            token.ThrowIfCancellationRequested();
            var current = read();
            if (!equality.Equals(current, confirmed))
            { Relinquish(); return new(false, name + "已被其他来源改变，本次让出，不证明是人为操作。", false); }
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(current, target)) write(target);
            token.ThrowIfCancellationRequested();
            var actual = read();
            if (!(acceptsTarget?.Invoke(actual, target) ?? equality.Equals(actual, target)) || !Owned)
            { UnconfirmedFailure = true; Relinquish(); return new(false, name + "写入结果未确认，本次让出，不重试。", false); }
            confirmed = actual;
            return new(true, name + "已读回确认；条件结束时仅在仍等于本次最后确认值时恢复。", true);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return new(false, name + "目标或读写未确认，本次让出；可能已修改，不自动恢复未确认写入。", false); }
    }
    // Fade-only: this read confirms the preceding write and checks ownership before the next write.
    internal bool AdvanceFade(T target, CancellationToken token)
    {
        if (!Owned) return false;
        try
        {
            if (!ConfirmFadeCore(token)) return false;
            token.ThrowIfCancellationRequested();
            if (equality.Equals(confirmed, target)) return true;
            pendingTarget = target; fadePending = true;
            write(target);
            token.ThrowIfCancellationRequested();
            return Owned;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    internal bool ConfirmFade(CancellationToken token)
    {
        if (!Owned) return false;
        try { return ConfirmFadeCore(token); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    private bool ConfirmFadeCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actual = read();
        token.ThrowIfCancellationRequested();
        if (fadePending)
        {
            if (!(acceptsTarget?.Invoke(actual, pendingTarget) ?? equality.Equals(actual, pendingTarget)) || !Owned)
            { UnconfirmedFailure = true; Relinquish(); return false; }
            confirmed = actual; fadePending = false;
        }
        else if (!equality.Equals(actual, confirmed) || !Owned) { Relinquish(); return false; }
        return true;
    }
    public RestorableSettingResult Release(CancellationToken token)
    {
        if (released) return new(true, name + "租约已释放。", false);
        if (!Owned) { released = true; return new(true, name + "本次已让出，保留当前设置；没有恢复写入。", false); }
        if (fadePending) { released = true; Relinquish(); return new(false, name + "渐变写入尚未确认，不恢复未确认值。", false); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(read(), confirmed))
            { Relinquish(); released = true; return new(true, name + "已被其他来源改变，保留当前设置，不恢复旧值。", false); }
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(confirmed, original)) write(original);
            token.ThrowIfCancellationRequested();
            var restored = equality.Equals(read(), original);
            released = true; Relinquish();
            return new(restored, restored ? name + "已读回确认恢复本轮最初设置。" : name + "恢复结果未确认；可能已修改，不重试。", false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { released = true; Relinquish(); return new(false, name + "原目标不可核验或恢复未确认；保留当前设置，不改替代目标。", false); }
    }
}
