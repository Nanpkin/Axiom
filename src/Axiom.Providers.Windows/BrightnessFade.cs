
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
        Action<CancellationToken> settle = cancellation => Wait(TimeSpan.FromMilliseconds(500), cancellation);
        try
        {
            var moving = new List<(BrightnessRestoreTarget Target, uint Value, uint Start)>();
            foreach (var (target, end) in targets)
            {
                token.ThrowIfCancellationRequested();
                var initial = target.Transaction.Confirmed;
                if (settings.Enabled && initial == end)
                {
                    // Confirm the live value once before excluding a zero-distance target.
                    target.Transaction.Apply(end, token, confirmUnchangedOnce: target.SettleAfterWrite);
                    token.ThrowIfCancellationRequested();
                }
                else moving.Add((target, end, initial));
            }
            var start = time.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!moving.Any(t => t.Target.Transaction.Owned)) return;
                if (settings.Enabled)
                {
                    // Wait after the preceding batch has finished: slow native calls never cause catch-up writes.
                    Wait(TimeSpan.FromMilliseconds(250), token);
                }
                token.ThrowIfCancellationRequested();
                var progress = settings.Enabled ? Math.Clamp(time.GetElapsedTime(start).TotalSeconds / settings.DurationSeconds, 0, 1) : 1;
                var eased = progress * progress * (3 - 2 * progress);
                foreach (var (target, end, initial) in moving)
                {
                    token.ThrowIfCancellationRequested();
                    if (!target.Transaction.Owned) continue;
                    var value = progress >= 1 ? end : (uint)Math.Round(initial + ((double)end - initial) * eased, MidpointRounding.AwayFromZero);
                    // Apply also checks ownership when rounding produces the same value; it does not write it again.
                    var hardwareFade = settings.Enabled && target.SettleAfterWrite;
                    target.Transaction.Apply(value, token, hardwareFade ? settle : null, confirmUnchangedOnce: hardwareFade);
                    token.ThrowIfCancellationRequested();
                }
                if (progress >= 1) return;
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var (target, _) in targets) target.Transaction.Relinquish();
            throw;
        }
    }
}
