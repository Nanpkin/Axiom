using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Axiom.Providers.Windows;

public sealed record WindowsTrayEntry(string ExecutablePath, string DisplayName, int IconCount);
public sealed record WindowsTrayCatalog(WindowsTrayEntry[] Entries, string? Notice);

[SupportedOSPlatform("windows")]
public static partial class WindowsTrayActions
{
    private static event Action<string, (nint Window, uint Id, Guid Guid)>? IconRemoved;

    // A subscription belongs only to an enabled continuous tool. Identity reads are confined to its configured paths.
    public static async Task WatchTargetsAsync(string[] paths, Action<bool, long, bool> publish, CancellationToken token)
    {
        if (paths.Length is < 1 or > 32) throw new ArgumentException("请选择 1–32 个目标程序。");
        using var notifications = new WindowsTrayEvents();
        var gate = new object();
        var previous = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
            _ => new HashSet<(nint Window, uint Id, Guid Guid)>(), StringComparer.OrdinalIgnoreCase);
        long counter = 0;
        void Removed(string path, (nint Window, uint Id, Guid Guid) identity)
        {
            lock (gate) if (previous.TryGetValue(path, out var identities)) identities.Remove(identity);
        }
        IconRemoved += Removed;
        try
        {
            bool Read()
            {
                var appeared = false;
                lock (gate)
                {
                    var registrations = ReadRegistrations();
                    foreach (var path in previous.Keys.ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        var matches = WindowsProgramControl.Matching(path);
                        try
                        {
                            var active = matches.Count == 0 ? [] : FindActive(path, registrations, matches, token).ToHashSet();
                            appeared |= active.Except(previous[path]).Any();
                            previous[path] = active;
                        }
                        finally { foreach (var process in matches) process.Dispose(); }
                    }
                }
                return appeared;
            }
            bool HasActive() { lock (gate) return previous.Values.Any(v => v.Count > 0); }
            Read(); var present = HasActive(); publish(true, counter, present);
            while (true)
            {
                await notifications.Events.Reader.ReadAsync(token);
                // Bounded channel merges native bursts. Removal alone never emits an execution opportunity.
                var appeared = Read(); var active = HasActive();
                if (appeared) counter = checked(counter + 1);
                if (appeared || present != active) publish(false, counter, active);
                present = active;
            }
        }
        finally { IconRemoved -= Removed; }
    }
    // Read on demand only. Historical settings are candidates, never proof that an icon is active.
    public static WindowsTrayCatalog ReadCatalog(CancellationToken token)
    {
        var registrations = ReadRegistrations();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = registrations.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    running.Add(process.ProcessName);
                    if (process.SessionId == current.SessionId && process.MainModule?.FileName is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.GetFullPath(path));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            }
        var entries = new List<WindowsTrayEntry>(); var skipped = 0;
        var activePaths = paths.Where(path => running.Contains(Path.GetFileNameWithoutExtension(path))).ToArray();
        if (activePaths.Length > 512) throw new IOException("运行中的程序路径超过当前识别范围，未返回截断名单。");
        foreach (var path in activePaths)
        {
            token.ThrowIfCancellationRequested();
            if (!running.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            List<Process>? matches = null;
            try
            {
                matches = WindowsProgramControl.Matching(path);
                if (matches.Count == 0) continue;
                var active = FindActive(path, registrations, matches, token);
                if (active.Length == 0) continue;
                // InitialTooltip is historical (RTSS even retains an old version), not a live label.
                var title = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription)) title = info.FileDescription.Trim();
                    else if (!string.IsNullOrWhiteSpace(info.ProductName)) title = info.ProductName.Trim();
                }
                catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException) { }
                entries.Add(new(path, title[..Math.Min(title.Length, 160)], active.Length));
                if (entries.Count > 128) throw new IOException("当前托盘程序超过 128 个，未返回截断名单。");
            }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            { skipped++; }
            finally { if (matches is not null) foreach (var process in matches) process.Dispose(); }
        }
        return new([.. entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
            skipped > 0 ? $"{skipped} 个程序无法核对权限或图标归属，未列入可选名单。" : null);
    }
    public static async Task<ProgramControlResult> HideAsync(string path, TimeSpan wait, CancellationToken token,
        TimeSpan registrationQuietPeriod = default)
    {
        if (wait < TimeSpan.FromSeconds(1) || wait > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(wait));
        if (!File.Exists(path)) return new(false, "目标程序不存在，请重新选择路径。");
        token.ThrowIfCancellationRequested();
        using var notifications = new WindowsTrayEvents();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(wait);
        HashSet<(nint Window, uint Id, Guid Guid)>? observed = null;
        long observedAt = 0;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // The appearance deadline must not turn a partially executed native operation into a no-op.
                var matches = WindowsProgramControl.Matching(path);
                var remainingQuiet = TimeSpan.Zero;
                try
                {
                    var visible = matches.Count == 0 ? [] : FindActive(path, ReadRegistrations(), matches, token);
                    if (visible.Length == 0)
                    {
                        observed = null;
                    }
                    else
                    {
                        if (registrationQuietPeriod > TimeSpan.Zero)
                        {
                            // A launcher can register a temporary icon and replace its HWND/UID
                            // while starting. Give the current set a quiet interval before hiding.
                            if (observed is null || !observed.SetEquals(visible))
                            {
                                observed = visible.ToHashSet();
                                observedAt = Stopwatch.GetTimestamp();
                            }
                            remainingQuiet = registrationQuietPeriod - Stopwatch.GetElapsedTime(observedAt);
                        }
                        if (remainingQuiet <= TimeSpan.Zero)
                            return await SetVisibilityAsync(path, visible, matches, false, notifications, token);
                    }
                }
                finally { foreach (var process in matches) process.Dispose(); }
                if (remainingQuiet <= TimeSpan.Zero)
                {
                    await notifications.Events.Reader.ReadAsync(deadline.Token);
                    continue;
                }
                using var quietDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                quietDeadline.CancelAfter(remainingQuiet);
                try { await notifications.Events.Reader.ReadAsync(quietDeadline.Token); }
                catch (OperationCanceledException) when (quietDeadline.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    // Re-read once at the quiet deadline even if Explorer sent no event.
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            var matches = WindowsProgramControl.Matching(path);
            try
            {
                var registered = matches.Count == 0 ? [] : FindRegistered(path, ReadRegistrations(), matches, token);
                // A late icon can be present even when Explorer delivered no usable event.
                var visible = registered.Where(i => i.Visibility == IconVisibility.Visible).Select(i => i.Identity).ToArray();
                if (visible.Length > 0) return await SetVisibilityAsync(path, visible, matches, false, notifications, token);
                return new(true, registered.Length > 0 ? "已确认目标托盘图标仍处于隐藏状态，无需修改。" : "未发现可识别图标，无需处理。")
                { Skipped = true, TrayHiddenConfirmed = registered.Length > 0 };
            }
            finally { foreach (var process in matches) process.Dispose(); }
        }
    }
    public static async Task<ProgramControlResult> ShowAsync(string path, TimeSpan wait, CancellationToken token)
    {
        if (wait < TimeSpan.FromSeconds(1) || wait > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(wait));
        if (!File.Exists(path)) return new(false, "目标程序不存在，请重新选择路径。");
        token.ThrowIfCancellationRequested();
        using var notifications = new WindowsTrayEvents();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(wait);
        var message = RegisterWindowMessage("TaskbarCreated");
        if (message == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var requested = new HashSet<(nint Window, int Process)>();
        var sent = false;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var matches = WindowsProgramControl.Matching(path);
                try
                {
                    var registered = matches.Count == 0 ? [] : FindRegistered(path, ReadRegistrations(), matches, token);
                    if (registered.Length > 0)
                    {
                        var shown = await SetVisibilityAsync(path, [.. registered.Select(i => i.Identity)], matches, true, notifications, token);
                        return shown with { Skipped = shown.Skipped && !sent };
                    }
                    // Only missing registrations need owner-driven recreation (including icons removed
                    // by old Axiom versions). A hidden registration is shown by clearing NIS_HIDDEN.
                    foreach (var window in matches.Count == 0 ? [] : FindOwnedWindows(matches, deadline.Token))
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (GetWindowThreadProcessId(window, out var pid) == 0 || !matches.Any(p => p.Id == pid && !p.HasExited)) continue;
                        if (!requested.Add((window, (int)pid))) continue;
                        if (!PostMessage(window, message, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        sent = true;
                    }
                }
                finally { foreach (var process in matches) process.Dispose(); }
                await notifications.Events.Reader.ReadAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            return new(false, sent ? "已请求程序重建托盘图标，但等待后未确认显示；程序可能不支持重建通知。" : "未找到可请求重建的目标程序窗口，未确认显示托盘图标。");
        }
    }
    private sealed record Registration(string Path, uint? Id, Guid Guid);
    private static Registration[] ReadRegistrations()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root is null) return [];
        var names = root.GetSubKeyNames();
        if (names.Length > 4096) throw new IOException("系统托盘记录超过当前支持范围，未修改图标。");
        var records = new List<Registration>();
        var folders = new Dictionary<Guid,string>();
        foreach (var name in names)
        {
            using var key = root.OpenSubKey(name); if (key is null) continue;
            if (key.GetValue("ExecutablePath") is not string raw || NormalizeRecordedPath(raw, folders) is not { } path) continue;
            var guidValue = key.GetValue("IconGuid");
            var guid = guidValue is byte[] { Length:16 } bytes ? new Guid(bytes) : guidValue is string text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
            uint? id = key.GetValue("UID") is int number ? unchecked((uint)number) : null;
            if (guid != Guid.Empty || id.HasValue) records.Add(new(path, id, guid));
        }
        return [.. records.Distinct()];
    }
    private static string? NormalizeRecordedPath(string raw, Dictionary<Guid,string> folders)
    {
        if (raw.Length > 32768) return null;
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith('{') && raw.IndexOf('}') is var end && end > 0 && Guid.TryParse(raw[..(end+1)],out var folder))
        {
            if (!folders.TryGetValue(folder, out var directory))
            {
                if (SHGetKnownFolderPath(ref folder, 0, 0, out var pointer) < 0) return null;
                try { directory = Marshal.PtrToStringUni(pointer) ?? ""; } finally { Marshal.FreeCoTaskMem(pointer); }
                folders[folder] = directory;
            }
            raw = directory + raw[(end+1)..];
        }
        try { return Path.IsPathFullyQualified(raw) && !raw.StartsWith(@"\\",StringComparison.Ordinal) ? Path.GetFullPath(raw) : null; }
        catch (ArgumentException) { return null; }
    }
    private enum IconVisibility { Missing, Hidden, Visible }
    private sealed record RegisteredIcon((nint Window, uint Id, Guid Guid) Identity, IconVisibility Visibility);

    private static IconVisibility ReadVisibility((nint Window, uint Id, Guid Guid) candidate, bool verifyHidden = true)
    {
        var identity = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = candidate.Window, Id = candidate.Id, Guid = candidate.Guid };
        var result = Shell_NotifyIconGetRect(ref identity, out var rect);
        if (result == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top) return IconVisibility.Visible;
        // S_FALSE/empty is not proof that a hidden registration still exists. For commands
        // that need that distinction, verify with NIM_MODIFY carrying identity flags only;
        // no image, tooltip, state, callback or version field is updated. Pure visible-only
        // catalog/watch reads never issue this probe or create their own update notifications.
        if (result == 1 && rect.Left == 0 && rect.Top == 0 && rect.Right == 0 && rect.Bottom == 0)
        {
            if (!verifyHidden) return IconVisibility.Missing;
            var probe = new IconData { Size = (uint)Marshal.SizeOf<IconData>(), Window = candidate.Window, Id = candidate.Id, Guid = candidate.Guid,
                Flags = candidate.Guid == Guid.Empty ? 0u : 32u, Tip = "", Info = "", Title = "" };
            return Shell_NotifyIcon(1, ref probe) ? IconVisibility.Hidden : IconVisibility.Missing;
        }
        if (result < 0) return IconVisibility.Missing;
        throw new IOException("系统未返回可确认的托盘图标可见性，未当作隐藏或不存在。");
    }

    private static async Task<ProgramControlResult> SetVisibilityAsync(string path, (nint Window, uint Id, Guid Guid)[] candidates,
        List<Process> matches, bool visible, WindowsTrayEvents notifications, CancellationToken token, Action? verify = null)
    {
        using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(token); confirmation.CancelAfter(TimeSpan.FromSeconds(3));
        var desired = visible ? IconVisibility.Visible : IconVisibility.Hidden;
        var written = 0;
        void VerifyOwner((nint Window, uint Id, Guid Guid) candidate)
        {
            verify?.Invoke();
            if (matches.All(p => p.HasExited) || candidate.Guid == Guid.Empty &&
                (GetWindowThreadProcessId(candidate.Window, out var pid) == 0 || !matches.Any(p => p.Id == pid && !p.HasExited)))
                throw new IOException("本轮图标所属程序或窗口已变化。");
        }
        try
        {
            foreach (var candidate in candidates)
            {
                confirmation.Token.ThrowIfCancellationRequested(); VerifyOwner(candidate);
                var current = ReadVisibility(candidate);
                VerifyOwner(candidate);
                if (current == IconVisibility.Missing) throw new IOException("本轮图标注册已不存在。");
                if (current == desired) continue;
                var data = new IconData { Size = (uint)Marshal.SizeOf<IconData>(), Window = candidate.Window, Id = candidate.Id, Guid = candidate.Guid,
                    Flags = 8u | (candidate.Guid == Guid.Empty ? 0u : 32u), State = visible ? 0u : 1u, Mask = 1u, Tip = "", Info = "", Title = "" };
                VerifyOwner(candidate); confirmation.Token.ThrowIfCancellationRequested();
                if (!Shell_NotifyIcon(1, ref data)) throw new IOException("Windows未接受图标可见性设置。");
                written++;
            }
            // Explorer can emit its visibility event before committing the rectangle, and
            // need not emit another event afterward. Allow one finite settle read, then only
            // real events and a final deadline read; never start a periodic scan.
            Task? settle = written > 0 ? Task.Delay(TimeSpan.FromMilliseconds(100), confirmation.Token) : null;
            Task<bool>? notification = null;
            while (true)
            {
                var complete = true;
                foreach (var candidate in candidates)
                {
                    // The confirmation deadline ends waiting, not the final read. Caller
                    // cancellation still forbids further native work, including this read.
                    token.ThrowIfCancellationRequested(); VerifyOwner(candidate);
                    var state = ReadVisibility(candidate);
                    VerifyOwner(candidate);
                    if (state == IconVisibility.Missing) throw new IOException("设置期间图标注册已移除，未追赶后来图标。");
                    complete &= state == desired;
                }
                if (complete) break;
                if (confirmation.IsCancellationRequested) throw new IOException("托盘可见性在确认期限结束时仍不匹配。");
                notification ??= notifications.Events.Reader.ReadAsync(confirmation.Token).AsTask();
                try
                {
                    if (settle is not null && await Task.WhenAny(notification, settle) == settle)
                    {
                        var once = settle; settle = null;
                        await once;
                    }
                    else { await notification; notification = null; }
                }
                catch (OperationCanceledException) when (confirmation.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // The next iteration performs the one final actual state read.
                    settle = null; notification = null;
                }
            }
            if (!visible && written > 0)
            {
                foreach (var candidate in candidates) IconRemoved?.Invoke(path, candidate);
                WindowsTrayEvents.Invalidate();
            }
            return new(true, $"{Path.GetFileName(path)}：" + (written > 0
                ? $"已确认{(visible ? "显示" : "隐藏")} {candidates.Length} 个图标。"
                : $"图标已处于{(visible ? "显示" : "隐藏")}状态，无需写入。")) { Skipped = written == 0, TrayHiddenConfirmed = !visible };
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        { return new(false, "托盘可见性设置或读回未确认；图标可能已改变，本次不自动重试或处理后来出现的图标。"); }
        finally { confirmation.Cancel(); }
    }
    private static (nint Window,uint Id,Guid Guid)[] FindActive(string path, Registration[] registrations, List<Process> matches, CancellationToken token) =>
        [.. FindRegistered(path, registrations, matches, token, includeHidden: false).Where(i => i.Visibility == IconVisibility.Visible).Select(i => i.Identity)];

    private static RegisteredIcon[] FindRegistered(string path, Registration[] registrations, List<Process> matches, CancellationToken token, bool includeHidden = true)
    {
        var selected = registrations.Where(r => r.Path.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length > 64) throw new InvalidOperationException("目标托盘记录过多，未修改图标。");
        var windows = FindOwnedWindows(matches, token);
        var candidates = new HashSet<(nint Window,uint Id,Guid Guid)>();
        foreach (var record in selected)
        {
            if (record.Guid != Guid.Empty)
            {
                if (registrations.Any(r => r.Guid == record.Guid && !r.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("托盘图标的程序归属存在冲突，未修改图标。");
                candidates.Add((0,0,record.Guid));
            }
            else foreach (var window in windows) candidates.Add((window,record.Id!.Value,Guid.Empty));
        }
        // RTSS/MFC may use its current HWND value as uID. Probe this single bounded convention
        // for each verified owned window. Visible candidates require S_OK/nonempty GetRect;
        // hidden candidates additionally require a successful fieldless registration check.
        foreach (var window in windows)
        {
            candidates.Add((window,unchecked((uint)window.ToInt64()),Guid.Empty));
            // NotifyIconSettings can retain an old UID when a framework recreates its NotifyIcon
            // (observed DSX: recorded 1, live 2). Probe only a small bounded range on verified owned
            // windows; visible GetRect or a verified hidden registration plus a second owner
            // check still precede every state change. History alone never proves registration.
            for (uint id = 0; id <= 16; id++) candidates.Add((window,id,Guid.Empty));
        }
        var active = new List<RegisteredIcon>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (matches.All(p => p.HasExited)) break;
            if (candidate.Guid == Guid.Empty && (GetWindowThreadProcessId(candidate.Window,out var pid) == 0 || !matches.Any(p => p.Id == pid && !p.HasExited))) continue;
            var visibility = ReadVisibility(candidate, includeHidden);
            if (matches.All(p => p.HasExited) || candidate.Guid == Guid.Empty &&
                (GetWindowThreadProcessId(candidate.Window, out var owner) == 0 || !matches.Any(p => p.Id == owner && !p.HasExited)))
                throw new IOException("读取托盘注册期间程序或窗口身份变化。");
            if (visibility != IconVisibility.Missing) active.Add(new(candidate, visibility));
            if (active.Count > 64) throw new InvalidOperationException("目标已注册图标过多，未修改图标。");
        }
        return [.. active];
    }
    private static HashSet<nint> FindOwnedWindows(List<Process> matches, CancellationToken token)
    {
        var ids = matches.Select(p => p.Id).ToHashSet(); var windows = new HashSet<nint>();
        bool Collect(nint window,nint state)
        {
            GetWindowThreadProcessId(window,out var pid);
            if (ids.Contains((int)pid)) windows.Add(window);
            return true;
        }
        EnumWindows((window,state) => { Collect(window,state); EnumChildWindows(window,Collect,0); return true; },0);
        nint message = 0; var count = 0;
        while ((message = FindWindowEx(-3,message,null,null)) != 0)
        { token.ThrowIfCancellationRequested(); if (++count > 4096) throw new InvalidOperationException("消息窗口数量超过识别范围，未修改图标。"); Collect(message,0); EnumChildWindows(message,Collect,0); }
        if (windows.Count > 256) throw new InvalidOperationException("目标窗口数量超过识别范围，未修改图标。");
        return windows;
    }
    private delegate bool EnumCallback(nint window,nint state);
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct IconData
    {
        public uint Size; public nint Window; public uint Id,Flags,Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,Mask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window,EnumCallback callback,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent,nint after,string? cls,string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool PostMessage(nint window,uint message,nint wparam,nint lparam);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id,uint flags,nint token,out nint path);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint operation,ref IconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id,out Rect rect);
}
