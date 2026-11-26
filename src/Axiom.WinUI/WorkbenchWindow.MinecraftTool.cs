using Axiom.Contracts;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private void BuildMinecraftTool()
    {
        var task = status?.Tasks.FirstOrDefault(t => t.Id == MinecraftTool.TaskId);
        var body = Column(12);
        body.Children.Add(Text("每次打开 PCL 启动器，检查 javaw.exe 在 NVIDIA 驱动中的 Vulkan/OpenGL 呈现方法，必要时设为“优先本机”。", 14, Ink));
        body.Children.Add(Text("作用于使用该 NVIDIA 程序配置的应用。全局设置保留；暂停后保留上次写入的值。", 13, Muted));
        if (task is not null && (task.Detection.CapabilityId != MinecraftTool.Launch || task.Action.CapabilityId != MinecraftTool.Apply))
        {
            body.Children.Add(Text("工具标识已由其他任务占用。", 13, Amber));
            content.Children.Add(Card(body));
            return;
        }
        var launcher = new TextBox { Header = "PCL 启动器文件名", Text = task?.Detection.Parameters.GetValueOrDefault("launcher") ?? MinecraftTool.DefaultLauncher };
        body.Children.Add(launcher);
        var java = new TextBox { Header = "Minecraft 的 javaw.exe（可选）", PlaceholderText = "留空自动识别驱动配置",
            Text = task?.Action.Parameters.GetValueOrDefault("javaPath") ?? "" };
        body.Children.Add(java);
        var pickJava = Action("选择 javaw.exe");
        pickJava.Click += async (_, _) =>
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is not null) java.Text = file.Path;
        };
        body.Children.Add(pickJava);
        var enabled = task is { Paused: false };
        body.Children.Add(Text(enabled ? "已启用，等待新的启动器进程。" : "已暂停。", 13, Muted));
        var actions = FlowActions();
        var save = Action(enabled ? "保存启动器" : "保存并启用");
        save.IsEnabled = reachable && status?.Capabilities.Any(c => c.Id == MinecraftTool.Apply) == true;
        save.Click += async (_, _) =>
        {
            var name = launcher.Text.Trim();
            if (!ProcessGroups.ValidProcessName(name)) { ShowNotice("请填写实际启动器的 .exe 文件名。"); return; }
            Axiom.Domain.AutomationTask configuration;
            try { configuration = MinecraftTool.Configure(name, false, task?.Version ?? 0, java.Text.Trim()); }
            catch (ArgumentException error) { ShowNotice(error.Message); return; }
            await MutateAsync(new(1, "save", Guid.NewGuid().ToString("N"), Task: configuration),
                "saved", "已启用，等待启动器进程。", false);
        };
        actions.Children.Add(save);
        if (enabled)
        {
            var pause = Action("暂停"); pause.IsEnabled = reachable;
            pause.Click += async (_, _) => await MutateAsync(new(1, "save", Guid.NewGuid().ToString("N"), Task: task! with { Paused = true }),
                "saved", "已暂停检查。", false);
            actions.Children.Add(pause);
        }
        var results = Action("查看执行结果");
        results.Click += async (_, _) => { page = Page.Activity; Render(); await RefreshAsync(); };
        actions.Children.Add(results);
        body.Children.Add(actions); content.Children.Add(Card(body));
    }
}
