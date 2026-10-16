using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class WindowOnceCatalogTests
{
    private const string TaskId = "10000000-0000-0000-0000-000000000001";
    private static TaskInvocation Invocation() => new(WindowOnceCapabilities.Due, 1,
        WindowOnceCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!));

    [Fact]
    public void ConfigurationIsStrictAndTaskScopedWithoutTurningAnEventIntoAContinuousCondition()
    {
        var invocation = Invocation(); var spec = WindowOnceCapabilities.Validate(invocation);
        Assert.Equal(new WindowOnceSpec(360, 1080, 127), spec);
        var resource = WindowOnceCapabilities.Resource(TaskId, spec);
        Assert.Equal(TaskId + "@06:00-18:00/7F/5/3", resource);
        Assert.Equal((TaskId, spec), WindowOnceCapabilities.ParseResource(resource));
        Assert.Contains("每天 06:00–18:00", WindowOnceCapabilities.Label(resource));
        var compiler = new WindowOnceTaskDetection();
        var condition = compiler.Compile(invocation, ResourceScope.Session("owner", 1), TaskId);
        Assert.Equal(ConditionKind.Event, condition.Kind);
        Assert.Equal(resource, Assert.Single(condition.Dependencies).ResourceId);
        Assert.False(compiler.Descriptor.IsStateCondition);
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation, ResourceScope.Session("owner", 1)));
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation, ResourceScope.Session("owner", 1), "not-a-task"));
        foreach (var (field, value) in new[] { ("startHour", "6.5"), ("startHour", "24"), ("startMinute", "-1"), ("endMinute", "60"), ("endHour", "6"), ("day0", "yes"), ("retryMinutes", "0"), ("retryMinutes", "1441"), ("retryMinutes", "1.5"), ("maxRetries", "101"), ("maxRetries", "-1") })
            Assert.Throws<ArgumentException>(() => WindowOnceCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem(field, value) }));
        var none = invocation.Parameters.ToBuilder(); for (var day = 0; day < 7; day++) none["day" + day] = "false";
        Assert.Throws<ArgumentException>(() => WindowOnceCapabilities.Validate(invocation with { Parameters = none.ToImmutable() }));
        Assert.Throws<ArgumentException>(() => WindowOnceCapabilities.Validate(invocation with { Parameters = invocation.Parameters.Add("script", "") }));
        foreach (var invalid in new[] { "06:00-18:00/7f/5/3", "06:00-18:00/00/5/3", "06:00-06:00/7F/5/3", "24:00-18:00/7F/5/3", "6:00-18:00/7F/5/3", "06:00-18:00/7F/05/3", "06:00-18:00/7F/5/101" })
            Assert.Throws<ArgumentException>(() => WindowOnceCapabilities.ParseResource(TaskId + "@" + invalid));
        var retryFields = WindowOnceCapabilities.Descriptor.Fields.Where(f => f.Group == "失败重试").ToArray();
        Assert.Equal(new[] { "retryMinutes", "maxRetries" }, retryFields.Select(f => f.Id));
        Assert.Equal(0, WindowOnceCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem("maxRetries", "0") }).MaxRetries);
        var maximumRetries = spec with { RetryMinutes = 1440, MaxRetries = 100 };
        Assert.Equal(maximumRetries, WindowOnceCapabilities.ParseResource(WindowOnceCapabilities.Resource(TaskId, maximumRetries)).Spec);
    }

    [Fact]
    public void LateBootIsImmediatelyEligibleButEndWeekdayAndSuccessfulDateRemainBounded()
    {
        var spec = new WindowOnceSpec(360, 1080, 127);
        var morning = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 16), spec.WindowDate(morning, TimeZoneInfo.Utc));
        Assert.Equal(morning, spec.Next(morning, TimeZoneInfo.Utc));
        Assert.Equal(morning.Date.AddHours(6), spec.Next(morning.AddHours(-3), TimeZoneInfo.Utc).UtcDateTime);
        Assert.Null(spec.WindowDate(morning.AddHours(10), TimeZoneInfo.Utc));
        Assert.Equal(morning.AddDays(1).AddHours(-2), spec.Next(morning, TimeZoneInfo.Utc, new(2026, 10, 16)));
        Assert.Equal(morning.AddHours(10), spec.NextBoundary(morning, TimeZoneInfo.Utc));
        var friday = new WindowOnceSpec(22 * 60, 60, 1 << (int)DayOfWeek.Friday);
        var saturday = new DateTimeOffset(2026, 10, 17, 0, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 16), friday.WindowDate(saturday, TimeZoneInfo.Utc));
        Assert.Null(friday.WindowDate(saturday.AddMinutes(30), TimeZoneInfo.Utc));
        Assert.Null(friday.WindowDate(saturday.AddHours(22), TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero), friday.Next(saturday, TimeZoneInfo.Utc, new(2026, 10, 16)));
        Assert.Contains("次日", WindowOnceCapabilities.Label(WindowOnceCapabilities.Resource(TaskId, friday)));
    }

    [Fact]
    public void MissingStartUsesRemainingWindowAndRepeatedHoursKeepTheSameSuccessfulDate()
    {
        var zone = SyntheticDst();
        var spring = new WindowOnceSpec(150, 240, 127);
        var beforeGap = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), spring.Next(beforeGap, zone));
        var missingEntireWindow = new WindowOnceSpec(130, 150, 127);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 10, 0, TimeSpan.Zero), missingEntireWindow.Next(beforeGap, zone));
        var repeated = new WindowOnceSpec(90, 150, 127);
        var beforeRepeat = new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(beforeRepeat.AddMinutes(90), repeated.Next(beforeRepeat, zone));
        var second = beforeRepeat.AddMinutes(165);
        Assert.Equal(new DateOnly(2026, 11, 1), repeated.WindowDate(second, zone));
        Assert.Equal(second, repeated.Next(second, zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), repeated.Next(second, zone, new(2026, 11, 1)));
        Assert.Equal(beforeRepeat.AddHours(2), repeated.NextBoundary(beforeRepeat.AddMinutes(105), zone));
        Assert.Equal(beforeRepeat.AddMinutes(150), repeated.Next(beforeRepeat.AddHours(2), zone));
    }

    [Fact]
    public async Task RegisteredCommandsStrictImportPausedTemplatesAndSaveUseTheSharedEditorContract()
    {
        var modules = BuiltInAutomationPlugins.Create();
        Assert.Single(modules.OfType<WindowOncePlugin>());
        var combinations = modules.SelectMany(m => m.Combinations).ToImmutableArray();
        Assert.Contains(new(WindowOnceCapabilities.Due, "program.start"), combinations);
        Assert.Contains(new(WindowOnceCapabilities.Due, NotificationCapabilities.Show), combinations);
        Assert.Equal(combinations.Where(c => c.DetectionId == DailyTimeCapabilities.Due).Select(c => c.ActionId).Distinct().Order(),
            combinations.Where(c => c.DetectionId == WindowOnceCapabilities.Due).Select(c => c.ActionId).Distinct().Order());
        Assert.DoesNotContain(combinations, c => c.DetectionId == WindowOnceCapabilities.Due &&
            (c.ActionId == KeepAwakeCapabilities.Hold || c.ActionId.EndsWith(".restore", StringComparison.Ordinal)));
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            combinations, true, "ready", "", DateTimeOffset.UtcNow, []);
        var templates = AutomationTemplates.Build(catalog);
        var template = Assert.Single(templates, t => t.Id == "window-once-reminder");
        Assert.True(template.Draft.Paused);
        Assert.Equal(Invocation().CapabilityId, template.Draft.Detection.CapabilityId);
        Assert.Equal(Invocation().Parameters.OrderBy(p => p.Key), template.Draft.Detection.Parameters.OrderBy(p => p.Key));
        Assert.Equal(DailyTimeCapabilities.Due, Assert.Single(templates, t => t.Id == "daily-document").Draft.Detection.CapabilityId);
        var actions = NotificationCapabilities.Descriptor.Fields.Where(f => f.DefaultValue is not null)
            .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("title", "").SetItem("body", "");
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "每天时段内提醒一次", "合成候选", Invocation(), new(NotificationCapabilities.Show, 1, actions));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused);
        foreach (var (field, value) in new[] { ("startHour", "6.5"), ("endHour", "6"), ("endMinute", "0.5"), ("retryMinutes", "0"), ("maxRetries", "0.5") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = proposal.Detection! with { Parameters = proposal.Detection.Parameters.SetItem(field, value) } }), recorder.Session, catalog));
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules)
        {
            foreach (var detection in module.Detections) compiler.Register(detection);
            foreach (var action in module.Actions) compiler.Register(action);
            foreach (var combination in module.Combinations) compiler.RegisterCombination(combination);
        }
        var completed = draft with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("title", "Synthetic").SetItem("body", "Synthetic reminder") } };
        Assert.Equal(ConditionKind.Event, compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)).Condition.Kind);
        var path = Path.Combine(Path.GetTempPath(), "axiom-window-once-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteAutomationTaskStore(path); await store.SaveAsync(completed, default);
            var saved = Assert.Single(await store.ListAsync());
            Assert.True(saved.Paused); Assert.Equal(completed.Detection.CapabilityId, saved.Detection.CapabilityId);
            Assert.Equal(completed.Detection.Parameters.OrderBy(p => p.Key), saved.Detection.Parameters.OrderBy(p => p.Key));
            Assert.Equal(completed.Action.Parameters.OrderBy(p => p.Key), saved.Action.Parameters.OrderBy(p => p.Key));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static TimeZoneInfo SyntheticDst() => TimeZoneInfo.CreateCustomTimeZone("synthetic-window-once-DST", TimeSpan.FromHours(-5), "synthetic", "standard", "daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2000, 1, 1), new(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
}
