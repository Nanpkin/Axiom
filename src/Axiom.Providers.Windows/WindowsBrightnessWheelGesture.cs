namespace Axiom.Providers.Windows;

public sealed record BrightnessWheelGestureResult(bool Success, long ConsumedSteps, string Reason);

// A finite Engine-authorized gesture. Only live input retargets it; idle completion releases handles
// without restoring the captured value. The independent Engine override keeps automation suspended.
public static class WindowsBrightnessWheelGesture
{
    private sealed class Transition(BrightnessRestoreTarget target, double percent)
    {
        internal readonly BrightnessRestoreTarget Target = target;
        internal double Percent = percent;
        internal uint Initial = target.Baseline, End = target.Baseline;
        internal TimeSpan Started, BurstStarted, LastWrite;
        internal bool HasWritten, Verified, Closing;
    }
    internal static BrightnessWheelGestureResult RunCaptured(BrightnessRestoreCapture capture, int step, int smoothMilliseconds,
        int frequency, long fromSteps, Func<long> latestSteps, CancellationToken token, TimeProvider? clock = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        if (step is < 1 or > 20 || smoothMilliseconds is < 50 or > 1000 || frequency is < 1 or > 60) throw new ArgumentException("滚轮亮度参数无效。");
        var time = clock ?? TimeProvider.System; var start = time.GetTimestamp(); var consumed = fromSteps;
        void Wait(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return;
            if (wait is not null) wait(delay, token); else Task.Delay(delay, time, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
        }
        var states = capture.Targets.Select(target =>
        {
            var min = target.Map(0); var max = target.Map(100);
            if (!min.HasValue || !max.HasValue || max <= min) throw new IOException("亮度范围未知。");
            return new Transition(target, ((double)target.Baseline - min.Value) * 100 / (max.Value - min.Value));
        }).ToArray();
        capture.UseToken(token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var latest = latestSteps();
            if (latest != consumed)
            {
                var delta = (decimal)latest - consumed;
                var changes = states.Select(state =>
                {
                    var next = Math.Clamp(state.Percent + (double)delta * step, 0, 100);
                    var end = state.Target.Map((int)Math.Round(next, MidpointRounding.AwayFromZero)) ?? throw new IOException("亮度映射未知。");
                    return (State: state, Percent: next, End: end);
                }).ToArray();
                foreach (var change in changes)
                {
                    var state = change.State; var end = change.End; var tx = state.Target.Transaction;
                    if (tx.HasPendingFadeWrite && Math.Sign((long)end - tx.FadeExpected) != Math.Sign((long)state.End - state.Initial))
                    {
                        Wait(state.LastWrite + TimeSpan.FromMilliseconds(100) - time.GetElapsedTime(start));
                        if (!tx.ConfirmHardwareBurst(token, time, (delay, _) => Wait(delay))) return new(false, consumed, "方向切换前结果未确认；已停止，不重试。");
                        state.Verified = true; state.Closing = false;
                    }
                }
                if (latestSteps() != latest) continue;
                foreach (var change in changes)
                {
                    var state = change.State;
                    state.Percent = change.Percent; state.Initial = state.Target.Transaction.FadeExpected; state.End = change.End; state.Started = time.GetElapsedTime(start);
                }
                consumed = latest;
            }
            var done = true;
            foreach (var state in states)
            {
                var tx = state.Target.Transaction;
                if (!tx.Owned) return new(false, consumed, "亮度控制已让出；不重试或覆盖未知值。");
                var elapsed = time.GetElapsedTime(start);
                if (tx.HasPendingFadeWrite && (tx.FadeExpected == state.End || state.Closing || tx.HardwareBurstFull || elapsed - state.BurstStarted >= TimeSpan.FromSeconds(1)))
                {
                    if (!tx.ConfirmHardwareBurst(token, time, (delay, _) => Wait(delay))) return new(false, consumed, "亮度段末结果未确认；不重试。");
                    state.Verified = true; state.Closing = false;
                }
                if (!tx.HasPendingFadeWrite && tx.Confirmed == state.End) continue;
                done = false;
                if (!state.Verified)
                {
                    if (!tx.ConfirmHardwareBurst(token, time, (delay, _) => Wait(delay))) return new(false, consumed, "当前亮度或授权未知；已让出。");
                    state.Verified = true;
                }
                elapsed = time.GetElapsedTime(start);
                uint Value()
                {
                    var p = Math.Clamp((time.GetElapsedTime(start) - state.Started).TotalMilliseconds / smoothMilliseconds, 0, 1);
                    return p >= 1 ? state.End : (uint)Math.Round(state.Initial + ((double)state.End - state.Initial) * p * p * (3 - 2 * p), MidpointRounding.AwayFromZero);
                }
                var value = Value();
                if (value == tx.FadeExpected) continue;
                // Finish each finite segment and each final target with an individually spaced write.
                var closing = tx.HasPendingFadeWrite && elapsed - state.BurstStarted >= TimeSpan.FromMilliseconds(800);
                var spacing = TimeSpan.FromMilliseconds(Math.Max(1000d / frequency, closing || value == state.End ? 100 : 0));
                if (state.HasWritten)
                {
                    var delay = state.LastWrite + spacing - elapsed;
                    if (tx.HasPendingFadeWrite)
                    {
                        var boundary = state.BurstStarted + TimeSpan.FromSeconds(1) - elapsed;
                        if (boundary < delay) delay = boundary;
                    }
                    Wait(delay);
                    if (latestSteps() != consumed) continue;
                    elapsed = time.GetElapsedTime(start);
                    if (tx.HasPendingFadeWrite && elapsed - state.BurstStarted >= TimeSpan.FromSeconds(1)) continue;
                    value = Value();
                    if (value == state.End) Wait(state.LastWrite + TimeSpan.FromMilliseconds(Math.Max(1000d / frequency, 100)) - elapsed);
                    if (latestSteps() != consumed) continue;
                    if (tx.HasPendingFadeWrite && time.GetElapsedTime(start) - state.BurstStarted >= TimeSpan.FromSeconds(1)) continue;
                }
                var pending = tx.HasPendingFadeWrite;
                var at = time.GetElapsedTime(start); value = Value();
                if (value == tx.FadeExpected) continue;
                if (!tx.WriteHardwareBurst(value, token)) return new(false, consumed, "亮度写入未接受或已取消；不重试。");
                if (!pending) state.BurstStarted = at;
                state.LastWrite = at; state.HasWritten = true; state.Closing = closing;
            }
            if (done && latestSteps() == consumed) return new(true, consumed, "滚轮亮度已严格确认；保持当下亮度，自动亮度仍由手动接管阻止。");
            Wait(TimeSpan.FromMilliseconds(16));
        }
    }
}

