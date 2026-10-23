using System.Globalization;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

internal sealed record BrightnessWheelRequest(string Target, int Step, int SmoothMilliseconds, int Frequency,
    long FromSteps, Func<long> LatestSteps, int PreferenceMinutes, CancellationToken Token);

// WMI and DXVA2 can refer to the same screen. An owned restoration blocks other Axiom brightness writes.
internal static class BrightnessNativeOwnership
{
    private static readonly object gate = new();
    private static Lease? active;
    private static bool manual;
    private static bool cleanupFailed;
    internal static IDisposable? TryAcquire(Action? abandon = null, bool manualGesture = false,
        Func<BrightnessWheelRequest, Task<BrightnessWheelGestureResult>>? wheel = null)
    {
        lock (gate)
        {
            if (cleanupFailed || active is not null || manual && !manualGesture || manualGesture && !manual) return null;
            return active = new Lease(abandon, wheel);
        }
    }
    internal static Task<BrightnessWheelGestureResult>? Coordinate(BrightnessWheelRequest request)
    {
        Func<BrightnessWheelRequest, Task<BrightnessWheelGestureResult>>? handler;
        lock (gate)
        {
            if (active is null) return null;
            handler = active.Wheel;
        }
        return handler is null ? Task.FromResult(new BrightnessWheelGestureResult(false, request.FromSteps,
            "当前亮度操作不支持场景修正；本次未修改，请等待其完成。")) : handler(request);
    }
    internal static Task DrainAsync() => Volatile.Read(ref active)?.Stopped.Task ?? Task.CompletedTask;
    internal static Task BeginManual()
    {
        Lease? current;
        lock (gate) { manual = true; current = active; }
        current?.Abandon?.Invoke();
        return current?.Stopped.Task ?? Task.CompletedTask;
    }
    internal static void FailCleanup() { lock (gate) cleanupFailed = true; }
    internal static void EndManual()
    {
        lock (gate)
        {
            if (active is not null || cleanupFailed) throw new InvalidOperationException("亮度原生工作或清理尚未确认，不能恢复自动控制。");
            manual = false;
        }
    }
    private sealed class Lease(Action? abandon, Func<BrightnessWheelRequest, Task<BrightnessWheelGestureResult>>? wheel) : IDisposable
    {
        internal readonly Action? Abandon = abandon;
        internal readonly Func<BrightnessWheelRequest, Task<BrightnessWheelGestureResult>>? Wheel = wheel;
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { lock (gate) if (ReferenceEquals(active, this)) { active = null; Stopped.TrySetResult(); } }
    }
}

internal sealed class BrightnessRestoreTarget
{
    internal readonly uint Baseline;
    internal readonly Func<int, uint?> Map;
    internal readonly ConditionalSettingTransaction<uint> Transaction;
    internal readonly bool HardwareBurst;
    internal uint? LastRead;
    internal BrightnessRestoreTarget(uint baseline, Func<int, uint?> map, Func<uint?> read, Func<uint, bool> write, bool hardwareBurst = false)
    {
        Baseline = baseline; Map = map; HardwareBurst = hardwareBurst;
        Transaction = new(baseline, () =>
        {
            LastRead = read(); return LastRead ?? throw new IOException("原亮度目标未知。");
        }, value => { if (!write(value)) throw new IOException("原亮度目标未接受设置。"); }, "屏幕亮度");
    }
}
internal sealed class BrightnessRestoreCapture(IReadOnlyList<BrightnessRestoreTarget> targets, int unknown, int unsupported,
    Action<CancellationToken> useToken, Action close) : IDisposable
{
    internal readonly IReadOnlyList<BrightnessRestoreTarget> Targets = targets;
    internal readonly int Unknown = unknown, Unsupported = unsupported;
    internal readonly Action<CancellationToken> UseToken = useToken;
    public void Dispose() => close();
}

// One worker keeps captured WMI objects/physical handles on their original thread. Requests are not polled or queued behind writes.
internal sealed class BrightnessRestoreSession : IRestorableSettingSession
{
    private sealed record Request(int? Percent, BrightnessFadeOptions Fade, CancellationToken Token, TaskCompletionSource<RestorableSettingResult> Result,
        BrightnessWheelRequest? Wheel = null, TaskCompletionSource<BrightnessWheelGestureResult>? Gesture = null);
    // A bounded, process-local preference per exact action configuration and original baseline.
    // Expiry is checked only on a new lease, never while somebody is still reading the same scene.
    private static readonly object preferenceGate = new();
    private static readonly Dictionary<string, (DateTimeOffset Until, int[] Percent)> preferences = [];
    private string? preferenceKey;
    private int[]? manualPercent;
    private CancellationTokenSource? manualCancellation;
    private readonly Channel<Request> requests = Channel.CreateBounded<Request>(1);
    private readonly SemaphoreSlim phase = new(1, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RestorableSettingResult> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource captureStop;
    private readonly CancellationTokenSource abandonStop = new();
    private readonly string? selection;
    private readonly TimeSpan timeout;
    private readonly TimeProvider time;
    private readonly Action<TimeSpan, CancellationToken>? wait;
    private BrightnessFadeOptions fade;
    private TimeSpan OperationTimeout => timeout + TimeSpan.FromSeconds(fade.Enabled ? fade.DurationSeconds : 0);
    private int terminal, closeRequested;
    private int abandoned;
    private BrightnessRestoreSession(string? selection, TimeSpan timeout, TimeProvider time, CancellationToken token, Action<TimeSpan, CancellationToken>? wait)
    { this.selection = selection; this.timeout = timeout; this.time = time; this.wait = wait; captureStop = CancellationTokenSource.CreateLinkedTokenSource(token); }
    internal static async Task<IRestorableSettingSession> CreateAsync(Func<CancellationToken, BrightnessRestoreCapture> capture,
        string? selection, TimeSpan timeout, CancellationToken token, TimeProvider? time = null, Action<TimeSpan, CancellationToken>? wait = null)
    {
        var session = new BrightnessRestoreSession(selection, timeout, time ?? TimeProvider.System, token, wait);
        token.ThrowIfCancellationRequested(); var ownership = BrightnessNativeOwnership.TryAcquire(session.Abandon, wheel: selection is null ? null : session.RunWheelAsync);
        if (ownership is null) { session.captureStop.Dispose(); session.abandonStop.Dispose(); throw new IOException("已有亮度操作、恢复租约或手动接管；请先等待释放或恢复自动控制。本次不排队、不修改。"); }
        try { new Thread(() => session.Run(capture, ownership)) { IsBackground = true, Name = "Axiom owned brightness restoration" }.Start(); }
        catch { ownership.Dispose(); session.captureStop.Dispose(); session.abandonStop.Dispose(); throw; }
        try { await session.ready.Task.WaitAsync(timeout, session.time, token); return session; }
        catch { session.RequestClose(); throw; } // the actual worker keeps ownership/resources until it returns and cleans up
    }
    public async Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
    {
        var requestedFade = BrightnessFadeOptions.Parse(parameters);
        if (!parameters.TryGetValue("percent", out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
            throw new ArgumentException("恢复租约亮度须为0–100整数。");
        if (selection is not null && (!parameters.TryGetValue("target", out var target) || target != selection))
        { Interlocked.Exchange(ref terminal, 1); return new(false, "本次亮度租约的屏幕范围已固定；不会改控新屏幕。", false); }
        if (Volatile.Read(ref terminal) != 0 || !phase.Wait(0)) return new(false, "亮度租约已让出或上次原生操作尚未返回；不重新捕获基线。", false);
        fade = requestedFade;
        // The session fixes its context at the first apply; different requested actions do not
        // silently reuse a manual correction. Baselines are added on the owning native thread.
        var key = string.Join(";", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        if (preferenceKey != key) { preferenceKey = key; manualPercent = null; }
        return await SubmitAsync(percent, token);
    }
    private async Task<BrightnessWheelGestureResult> RunWheelAsync(BrightnessWheelRequest wheel)
    {
        if (wheel.Target != selection || Volatile.Read(ref terminal) != 0)
            return new(false, wheel.FromSteps, "滚轮范围与当前租约不一致或租约正在恢复；未修改，也未改控其他屏幕。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(wheel.Token, abandonStop.Token, captureStop.Token);
        wheel = wheel with { Token = lifetime.Token };
        await phase.WaitAsync(wheel.Token);
        if (Volatile.Read(ref terminal) != 0 || preferenceKey is null)
        { phase.Release(); return new(false, wheel.FromSteps, "原场景已结束或首次设置尚未确认；本次手势未修改。"); }
        var receipt = new TaskCompletionSource<BrightnessWheelGestureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unused = new TaskCompletionSource<RestorableSettingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref manualCancellation, lifetime);
        try
        {
            if (Volatile.Read(ref terminal) != 0) lifetime.Cancel();
            if (!requests.Writer.TryWrite(new(null, fade, wheel.Token, unused, wheel, receipt)))
            { phase.Release(); return new(false, wheel.FromSteps, "亮度租约已结束；未修改。"); }
            return await receipt.Task.WaitAsync(wheel.Token);
        }
        finally { Interlocked.CompareExchange(ref manualCancellation, null, lifetime); }
    }
    public async Task<RestorableSettingResult> ReleaseAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref terminal, 1);
        try { Volatile.Read(ref manualCancellation)?.Cancel(); } catch (ObjectDisposedException) { }
        if (stopped.Task.IsCompleted) return await stopped.Task;
        try { await phase.WaitAsync(token).WaitAsync(OperationTimeout, time, token); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        { RequestClose(); return new(false, "亮度释放等待原生操作结束；恢复尚未确认，句柄与准入仍保留。", false); }
        if (stopped.Task.IsCompleted) { phase.Release(); return await stopped.Task; }
        return await SubmitAsync(null, token);
    }
    private async Task<RestorableSettingResult> SubmitAsync(int? percent, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, abandonStop.Token);
        var result = new TaskCompletionSource<RestorableSettingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.Writer.TryWrite(new(percent, fade, lifetime.Token, result)))
        { phase.Release(); return new(false, "亮度租约已结束；不会写入或重新捕获。", false); }
        try { return await result.Task.WaitAsync(OperationTimeout, time, token); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            Interlocked.Exchange(ref terminal, 1);
            return new(false, "亮度操作取消或超时，可能已设置但未确认；不重试，原生返回后才释放资源。", false);
        }
        finally { lifetime.Cancel(); } // worker, rather than the awaiting caller, releases phase admission
    }
    private void RequestClose()
    {
        Interlocked.Exchange(ref terminal, 1); Interlocked.Exchange(ref closeRequested, 1);
        try { captureStop.Cancel(); } catch (ObjectDisposedException) { } requests.Writer.TryComplete();
    }
    private void Abandon()
    {
        Interlocked.Exchange(ref abandoned, 1);
        try { abandonStop.Cancel(); } catch (ObjectDisposedException) { }
        RequestClose();
    }
    public async ValueTask DisposeAsync() { RequestClose(); await stopped.Task; }
    private void Run(Func<CancellationToken, BrightnessRestoreCapture> create, IDisposable ownership)
    {
        BrightnessRestoreCapture? capture = null; Request? release = null;
        RestorableSettingResult receipt = new(false, "未取得已知亮度基线，本次未修改。", false); var releaseAttempted = false;
        try
        {
            capture = create(captureStop.Token); captureStop.Token.ThrowIfCancellationRequested();
            if (capture.Targets.Count == 0) throw new IOException("没有可确认支持的已知亮度目标；本次未修改。");
            ready.TrySetResult();
            while (requests.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult() && requests.Reader.TryRead(out var request))
            {
                if (request.Wheel is { } wheel)
                {
                    BrightnessWheelGestureResult result = new(false, wheel.FromSteps, "场景调节未确认。");
                    try
                    {
                        capture.UseToken(wheel.Token);
                        result = WindowsBrightnessWheelGesture.RunCaptured(capture, wheel.Step, wheel.SmoothMilliseconds, wheel.Frequency,
                            wheel.FromSteps, wheel.LatestSteps, wheel.Token, time, wait, retainBaseline: true);
                        if (result.Success)
                        {
                            manualPercent = capture.Targets.Select(t => ToPercent(t, t.Transaction.Confirmed)).ToArray();
                            lock (preferenceGate)
                            {
                                var key = ContextKey(capture);
                                if (wheel.PreferenceMinutes > 0)
                                {
                                    if (preferences.Count >= 16 && !preferences.ContainsKey(key))
                                        preferences.Remove(preferences.MinBy(p => p.Value.Until).Key);
                                    preferences[key] = (time.GetUtcNow().AddMinutes(wheel.PreferenceMinutes), manualPercent);
                                }
                                else preferences.Remove(key);
                            }
                        }
                    }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                    { result = new(false, wheel.FromSteps, "场景手动调节取消或未确认；原基线保留，不补写或重抓基线。"); }
                    finally
                    {
                        if (!result.Success) Interlocked.Exchange(ref terminal, 1);
                        phase.Release();
                    }
                    request.Gesture!.TrySetResult(result);
                    continue;
                }
                if (request.Percent is null)
                {
                    release = request; releaseAttempted = true; receipt = Restore(capture, request.Fade, request.Token); break;
                }
                CompleteApply(phase, request.Result, () =>
                {
                    try
                    {
                        var applied = Apply(capture, request.Percent.Value, request.Fade, request.Token);
                        if (!applied.Owned) Interlocked.Exchange(ref terminal, 1);
                        return applied;
                    }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                    {
                        Interlocked.Exchange(ref terminal, 1); return new(false, "亮度设置未确认；不重新捕获或重试，结束时仅恢复仍归本次租约所有的已确认值。", false);
                    }
                });
                if (Volatile.Read(ref closeRequested) != 0) break;
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { ready.TrySetException(error); receipt = new(false, "亮度捕获或恢复未确认；原目标可能不可用，不会改控新屏幕。", false); }
        finally
        {
            try
            {
                if (capture is not null && Volatile.Read(ref abandoned) != 0)
                {
                    foreach (var target in capture.Targets) target.Transaction.Relinquish();
                    receipt = new(true, "已明确放弃本轮亮度恢复，保留当下亮度；原生工作及资源已结束。", false);
                }
                else if (capture is not null && !releaseAttempted)
                {
                    using var cleanup = new CancellationTokenSource(OperationTimeout); receipt = Restore(capture, fade, cleanup.Token);
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { receipt = new(false, "亮度恢复未确认；不会重试或改控新屏幕。", false); }
            try { capture?.Dispose(); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { receipt = new(false, receipt.Reason + " 亮度资源释放未确认。", false); }
            requests.Writer.TryComplete(); Interlocked.Exchange(ref terminal, 1);
            while (requests.Reader.TryRead(out var pending))
            {
                pending.Result.TrySetResult(new(false, "亮度租约已结束。", false));
                pending.Gesture?.TrySetResult(new(false, pending.Wheel!.FromSteps, "亮度租约已结束。")); phase.Release();
            }
            if (release is not null) phase.Release();
            ownership.Dispose(); captureStop.Dispose(); abandonStop.Dispose(); stopped.TrySetResult(receipt); release?.Result.TrySetResult(receipt);
        }
    }
    internal static void CompleteApply(SemaphoreSlim admission, TaskCompletionSource<RestorableSettingResult> completion,
        Func<RestorableSettingResult> apply)
    {
        RestorableSettingResult receipt;
        try { receipt = apply(); }
        finally { admission.Release(); }
        // An awaiting consumer may run before TrySetResult returns, even with asynchronous continuations.
        completion.TrySetResult(receipt);
    }
    private RestorableSettingResult Apply(BrightnessRestoreCapture capture, int percent, BrightnessFadeOptions settings, CancellationToken token)
    {
        capture.UseToken(token); var confirmed = 0; var failed = capture.Unknown; var relinquished = 0;
        if (manualPercent is null)
            lock (preferenceGate)
                if (preferences.TryGetValue(ContextKey(capture), out var saved) && saved.Until > time.GetUtcNow() && saved.Percent.Length == capture.Targets.Count)
                    manualPercent = saved.Percent;
        var transitions = new List<(BrightnessRestoreTarget Target, uint Value)>();
        var index = 0;
        foreach (var target in capture.Targets)
        {
            var requestedPercent = manualPercent is { } chosen ? chosen[index] : percent; index++;
            token.ThrowIfCancellationRequested();
            if (!target.Transaction.Owned) { relinquished++; continue; }
            var requested = target.Map(requestedPercent);
            if (!requested.HasValue) { target.Transaction.Relinquish(); failed++; continue; }
            transitions.Add((target, requested.Value));
        }
        BrightnessFade.Apply(transitions, settings, token, time, wait);
        confirmed += transitions.Count(t => t.Target.Transaction.Owned);
        failed += transitions.Count(t => !t.Target.Transaction.Owned);
        var owned = confirmed > 0 && failed == 0 && relinquished == 0;
        return new(owned, $"亮度条件设置{(owned ? "已确认" : confirmed > 0 ? "部分完成" : "未确认")}：{confirmed}块确认，{capture.Unsupported}块已知不支持跳过，{failed}个目标未知或失败，{relinquished}块已让出；{Levels(capture)}；{(manualPercent is null ? "" : "使用本配置的短期手动偏好；")}外部改变或未确认不覆盖，本次基线保留。", owned);
    }
    private string ContextKey(BrightnessRestoreCapture capture) => preferenceKey + "|" + string.Join(",", capture.Targets.Select(t => t.Baseline));
    private static int ToPercent(BrightnessRestoreTarget target, uint value)
    {
        var min = target.Map(0) ?? throw new IOException("亮度范围未知。");
        var max = target.Map(100) ?? throw new IOException("亮度范围未知。");
        if (max <= min) throw new IOException("亮度范围无效。");
        return (int)Math.Clamp(Math.Round(((double)value - min) * 100 / (max - min)), 0, 100);
    }
    private RestorableSettingResult Restore(BrightnessRestoreCapture capture, BrightnessFadeOptions settings, CancellationToken token)
    {
        capture.UseToken(token); var restored = 0; var skipped = 0; var failed = 0;
        foreach (var target in capture.Targets) target.Transaction.ConfirmInterruptedFadeForRelease(token);
        if (settings.Enabled)
            BrightnessFade.Apply(capture.Targets.Where(t => t.Transaction.Owned).Select(t => (t, t.Baseline)).ToArray(), settings, token, time, wait);
        foreach (var target in capture.Targets)
        {
            token.ThrowIfCancellationRequested(); var previouslyOwned = target.Transaction.Owned;
            // The fade's final Apply has already checked ownership and read back the exact baseline.
            var receipt = settings.Enabled && target.Transaction.TryCompleteConfirmedRelease()
                ? new RestorableSettingResult(true, "渐变已读回确认原基线，租约已释放。", false)
                : target.Transaction.Release(token);
            token.ThrowIfCancellationRequested();
            if (settings.Enabled && target.Transaction.UnconfirmedFailure) failed++;
            else if (!previouslyOwned || receipt.Success && target.LastRead != target.Baseline) skipped++;
            else if (receipt.Success) restored++;
            else failed++;
        }
        return new(failed == 0, $"亮度结束恢复：{restored}块基线读回确认，{skipped}块其他来源改变/设备未知/未确认值未覆盖，{failed}块恢复未确认；{Levels(capture)}；不重试、不改控新屏幕。", false);
    }
    private static string Levels(BrightnessRestoreCapture capture) => string.Join("；", capture.Targets.Take(8).Select((t, i) =>
    {
        string Percent(uint value) => t.Map(0) is { } min && t.Map(100) is { } max && max > min
            ? ((double)(value - min) * 100 / (max - min)).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知";
        return $"屏幕{i + 1}原亮度{Percent(t.Baseline)}、最后读回{(t.LastRead is { } read ? Percent(read) : "未知")}";
    }));
}
