using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class AutomationTemplateTests
{
    private static IAutomationPlugin[] Modules() => BuiltInAutomationPlugins.Create();
    private static AutomationSnapshot Catalog() => new([], [.. Modules().SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))], [.. Modules().SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
    [Fact]
    public void EveryAvailableRecipeIsPausedUnsavedAndCompilesAfterTheUserSelectsItsRequiredObjects()
    {
        var catalog = Catalog(); var templates = AutomationTemplates.Build(catalog); Assert.Equal(67, templates.Length);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in Modules()) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        foreach (var template in templates)
        {
            Assert.True(template.Draft.Paused); Assert.Equal(0, template.Draft.Version);
            if (template.Id is not ("battery-low-brightness-restore" or "standard-saver-hardware-brightness-restore" or "foreground-cover-keep-awake" or "standard-saver-dim-panel" or "windows-saver-dim-panel" or "session-absent-mute" or "public-network-reminder" or "workday-system-sound" or "current-media-awake" or "lock-pause-current-media" or "screen-off-pause-current-media" or "lock-display-off" or "lock-mute" or "screen-off-mute" or "battery-power-saver" or "battery-dim-panel" or "unmuted-microphone-awake" or "low-battery-dim-panel" or "plugged-in-awake" or "multi-display-awake")) Assert.Throws<ArgumentException>(() => compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
            else Assert.NotNull(compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
            var completed = template.Draft with { Detection = Complete(template.Draft.Detection), Action = Complete(template.Draft.Action) };
            Assert.NotNull(compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)));
            var editor = new Axiom.WinUI.TaskEditorState(); editor.LoadDraft(template.Draft); Assert.True(editor.Dirty);
        }
        Assert.Equal("graceful", templates.Single(t => t.Id == "cleanup-companion").Draft.Action.Parameters["mode"]);
        Assert.Equal("latest", templates.Single(t => t.Id == "cleanup-companion").Draft.Action.Parameters["instances"]);
        Assert.False(templates.Single(t => t.Id == "wifi-work-folder").Draft.Detection.Parameters.ContainsKey("profileNames"));
        Assert.False(templates.Single(t => t.Id == "usb-dock-tools").Draft.Detection.Parameters.ContainsKey("deviceInstanceIds"));
        Assert.DoesNotContain(templates[0].Draft.Id, AutomationTemplates.Build(catalog).Select(t => t.Draft.Id));

        TaskInvocation Complete(TaskInvocation invocation)
        {
            var values = invocation.Parameters.ToBuilder();
            foreach (var field in catalog.Capabilities.Single(c => c.Id == invocation.CapabilityId).Fields.Where(f => f.Required && !values.ContainsKey(f.Id)))
                values[field.Id] = field.Kind switch
                {
                    TaskFieldKind.Number => field.Id switch { "year" => "2090", "month" => "10", "day" => "8", _ => throw new InvalidOperationException() },
                    TaskFieldKind.DirectoryPath => Path.GetTempPath(),
                    TaskFieldKind.ProcessSelection => "Example.exe",
                    TaskFieldKind.FilePath or TaskFieldKind.ExecutableSelection => Path.Combine(Path.GetTempPath(), "Example.exe"),
                    TaskFieldKind.HotkeyList => "Ctrl+Alt+F9",
                    TaskFieldKind.TextList => invocation.CapabilityId is OpenTargetBatchCapabilities.Open or WorkPreparationCapabilities.Prepare ? "网页 | https://example.invalid/work" : invocation.CapabilityId == UsbDeviceCapabilities.Present ? @"USB\VID_1234&PID_5678\SYNTHETIC_A" : field.EditAsList ? "Synthetic Office" : "请休息片刻",
                    TaskFieldKind.Text => invocation.Parameters.GetValueOrDefault("kind") == "document" ? Path.Combine(Path.GetTempPath(), "SyntheticPlan.pdf") : invocation.CapabilityId == NotificationCapabilities.Show ? "请休息片刻" : invocation.Parameters.GetValueOrDefault("kind") == "url" ? "https://example.invalid/reminder" : Path.GetTempPath(),
                    _ => throw new InvalidOperationException("Uncovered template parameter kind.")
                };
            return invocation with { Parameters = values.ToImmutable() };
        }
    }
    [Fact]
    public void RecipesAreHiddenWhenTheLiveRegistryCannotSupplyTheirCombinationOrChoice()
    {
        var catalog = Catalog(); Assert.Empty(AutomationTemplates.Build(catalog with { Combinations = [] }));
        Assert.DoesNotContain(AutomationTemplates.Build(catalog with { Capabilities = [.. catalog.Capabilities.Where(c => c.Id != WindowCapabilities.State)] }), t => t.Draft.Action.CapabilityId == WindowCapabilities.State);
        var reduced = catalog with { Capabilities = [.. catalog.Capabilities.Select(c => c.Id != WindowCapabilities.State ? c : c with { Fields = [.. c.Fields.Select(f => f.Id == "state" ? f with { Choices = [] } : f)] })] };
        Assert.DoesNotContain(AutomationTemplates.Build(reduced), t => t.Draft.Action.CapabilityId == WindowCapabilities.State);
    }
}
