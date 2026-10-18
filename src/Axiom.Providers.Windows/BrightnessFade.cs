
namespace Axiom.Providers.Windows;

public readonly record struct BrightnessFadeOptions(bool Enabled = false, int DurationSeconds = 3)
{
    internal TimeSpan OperationTimeout => TimeSpan.FromSeconds(10 + (Enabled ? DurationSeconds : 0));
    public static BrightnessFadeOptions Parse(IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = parameters.GetValueOrDefault("fadeEnabled", "false");
        var duration = parameters.GetValueOrDefault("fadeDurationSeconds", "3");
        if (enabled is not ("true" or "false") ||
            !int.TryParse(duration, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 15)
            throw new ArgumentException("亮度渐变参数无效。");
        return new(enabled == "true", seconds);
    }
    internal void Validate()
    {
        if (Enabled && DurationSeconds is < 1 or > 15) throw new ArgumentException("亮度渐变时长须为1–15秒。");
    }
}

// A finite transition on already captured targets, on their existing native owner thread.
internal static class BrightnessFade
{
    internal static void Apply(IReadOnlyList<(BrightnessRestoreTarget Target, uint Value)> targets,
        BrightnessFadeOptions settings, CancellationToken token, TimeProvider? clock = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        settings.Validate();
        var time = clock ?? TimeProvider.System;
        void Wait(TimeSpan delay, CancellationToken cancellation)
        {
            if (wait is not null) wait(delay, cancellation);
            else Task.Delay(delay, time, cancellation).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
        }
        try
        {
            if (!settings.Enabled)
            {
                foreach (var (target, end) in targets)
                { token.ThrowIfCancellationRequested(); target.Transaction.Apply(end, token); token.ThrowIfCancellationRequested(); }
                return;
            }
            var moving = new List<(BrightnessRestoreTarget Target, uint Value, uint Start)>();
            var completed = new HashSet<BrightnessRestoreTarget>();
            var bursts = new Dictionary<BrightnessRestoreTarget, TimeSpan>();
            foreach (var (target, end) in targets)
            {
                token.ThrowIfCancellationRequested();
                var initial = target.Transaction.Confirmed;
                if (initial == end)
                {
                    // Confirm the live value once before excluding a zero-distance target.
                    target.Transaction.ConfirmFade(token);
                    token.ThrowIfCancellationRequested();
                }
                else moving.Add((target, end, initial));
            }
            var start = time.GetTimestamp();
            var next = TimeSpan.Zero;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!moving.Any(t => t.Target.Transaction.Owned && !completed.Contains(t.Target))) return;
                var remaining = next - time.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero) Wait(remaining, token);
                token.ThrowIfCancellationRequested();
                var frameStarted = time.GetElapsedTime(start);
                var progress = Math.Clamp(frameStarted.TotalSeconds / settings.DurationSeconds, 0, 1);
                var eased = progress * progress * (3 - 2 * progress);
                foreach (var (target, end, initial) in moving)
                {
                    token.ThrowIfCancellationRequested();
                    if (!target.Transaction.Owned || completed.Contains(target)) continue;
                    var value = progress >= 1 ? end : (uint)Math.Round(initial + ((double)end - initial) * eased, MidpointRounding.AwayFromZero);
                    if (target.HardwareBurst)
                    {
                        var elapsed = time.GetElapsedTime(start);
                        if (target.Transaction.HasPendingFadeWrite && target.Transaction.FadeExpected == end)
                        {
                            target.Transaction.ConfirmFade(token); bursts.Remove(target);
                        }
                        else
                        {
                            var pending = target.Transaction.HasPendingFadeWrite;
                            var continuing = pending && bursts.TryGetValue(target, out var burstStarted) && elapsed - burstStarted < TimeSpan.FromSeconds(1);
                            if (pending && !continuing || value != target.Transaction.FadeExpected)
                            {
                                // One boundary read confirms the previous segment and checks ownership for the next write.
                                var segmentStarted = time.GetElapsedTime(start);
                                target.Transaction.AdvanceFade(value, token, continueBurst: continuing);
                                if (target.Transaction.Owned && target.Transaction.HasPendingFadeWrite && !continuing) bursts[target] = segmentStarted;
                                else if (!target.Transaction.HasPendingFadeWrite) bursts.Remove(target);
                            }
                        }
                    }
                    else if (target.Transaction.HasPendingFadeWrite || value != target.Transaction.Confirmed)
                        target.Transaction.AdvanceFade(value, token);
                    token.ThrowIfCancellationRequested();
                    if (target.Transaction.Owned && !target.Transaction.HasPendingFadeWrite && target.Transaction.Confirmed == end)
                        completed.Add(target);
                }
                // Slow native work skips expired frames; no backlog and no added hardware settling delay.
                next = frameStarted + TimeSpan.FromMilliseconds(16);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var (target, _) in targets) target.Transaction.Relinquish();
            throw;
        }
    }
}
