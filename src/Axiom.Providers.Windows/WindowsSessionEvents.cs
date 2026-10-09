using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// One hidden top-level window per Agent, shared by rule and explicit observation leases.
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(64);
    private readonly Channel<byte> clocks = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly bool clockOnly;
    private readonly bool usbOnly;
    private readonly bool powerOnly;
    private readonly bool displayOnly;
    private readonly bool screenPowerOnly;
    private readonly bool schemeOnly;
    private readonly bool saverOnly;
    private volatile bool failed;
    private bool disposed;
    private readonly Hub owner;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public ChannelReader<string> Events => queue.Reader;
    public ChannelReader<byte> ClockChanges => clocks.Reader;
    public bool Failed => failed;
    internal nint NotificationWindow => owner.Window;
    public WindowsSessionEvents(bool clockOnly = false, bool usbOnly = false, bool powerOnly = false, bool displayOnly = false, bool screenPowerOnly = false, bool schemeOnly = false, bool saverOnly = false)
    {
        this.clockOnly = clockOnly; this.usbOnly = usbOnly; this.powerOnly = powerOnly; this.displayOnly = displayOnly; this.screenPowerOnly = screenPowerOnly; this.schemeOnly = schemeOnly; this.saverOnly = saverOnly;
        lock (Gate)
        {
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
        }
        // Do not block the gate while native subscription setup may deliver a callback.
        try
        {
            owner.Ready.Task.GetAwaiter().GetResult();
            if (owner.Stopped.Task.IsCompleted) throw new IOException("系统通知源已停止。");
            if (powerOnly && !owner.PowerNotificationsAvailable) throw new IOException("无法订阅供电来源变化通知。");
            if (saverOnly && !owner.SaverNotificationsAvailable) throw new IOException("无法订阅Windows节能状态报告。");
            if (schemeOnly && !owner.SchemeNotificationsAvailable) throw new IOException("无法订阅当前电源方案通知。");
            if (screenPowerOnly && !owner.ScreenNotificationsAvailable) throw new IOException("无法订阅当前会话屏幕电源通知。");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); clocks.Writer.TryComplete();
            owner.Clients.Remove(this);
            if (owner.Clients.Count == 0)
            {
                last = true;
                if (ReferenceEquals(shared, owner)) shared = null;
                if (!owner.Stopped.Task.IsCompleted && owner.Window != 0) PostMessage(owner.Window, 0x10, 0, 0);
            }
        }
        if (last) owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsSessionEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal nint Window;
        internal bool PowerNotificationsAvailable;
        internal bool ScreenNotificationsAvailable;
        internal bool SchemeNotificationsAvailable;
        internal bool SaverNotificationsAvailable;
        private readonly Procedure procedure;
        private byte supply = 255;
        private readonly BatteryThresholdEvents battery = new();
        private static readonly Guid UsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        private static readonly Guid BatteryPercentage = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
        private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
        private static readonly Guid PowerSavingStatus = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");
        private static readonly Guid ActivePowerScheme = new("31F9F286-5084-42FE-B720-2B0264993763");
        private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
        private bool network;
        internal Hub() { procedure = Handle; new Thread(Run) { IsBackground = true, Name = "Axiom session and power events" }.Start(); }
        private void Run()
        {
            var name = "AxiomSystemEvents-" + Guid.NewGuid().ToString("N");
            var instance = GetModuleHandle(null);
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Name = name, Instance = instance };
            var registered = false; var notified = false; var networkSubscribed = false; nint powerNotification = 0, deviceNotification = 0, supplyNotification = 0, screenNotification = 0, schemeNotification = 0, saverNotification = 0;
            try
            {
                if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); registered = true;
                Window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
                if (Window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WTSRegisterSessionNotification(Window, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); notified = true;
                var filter = new DeviceInterface { Size = (uint)Marshal.SizeOf<DeviceInterface>(), DeviceType = 5, ClassGuid = UsbDevice };
                deviceNotification = RegisterDeviceNotification(Window, ref filter, 0);
                if (deviceNotification == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法订阅USB设备类别通知。");
                var setting = BatteryPercentage;
                powerNotification = RegisterPowerSettingNotification(Window, ref setting, 0);
                if (powerNotification == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法订阅电量变化通知。");
                setting = AcDcPowerSource;
                supplyNotification = RegisterPowerSettingNotification(Window, ref setting, 0);
                PowerNotificationsAvailable = supplyNotification != 0; // failure disables only the new power-state consumer
                setting = SessionDisplayStatus;
                screenNotification = RegisterPowerSettingNotification(Window, ref setting, 0);
                ScreenNotificationsAvailable = screenNotification != 0; // no inference from lock, brightness, or topology
                setting = ActivePowerScheme;
                schemeNotification = RegisterPowerSettingNotification(Window, ref setting, 0);
                SchemeNotificationsAvailable = schemeNotification != 0; // additional registration failure affects only opt-in consumers
                setting = PowerSavingStatus;
                saverNotification = RegisterPowerSettingNotification(Window, ref setting, 0);
                SaverNotificationsAvailable = saverNotification != 0; // legacy battery-saver report; not the Energy Saver intensity API
                if (GetSystemPowerStatus(out var initial)) supply = initial.AcLine;
                network = NetworkInterface.GetIsNetworkAvailable();
                NetworkChange.NetworkAvailabilityChanged += NetworkChanged; networkSubscribed = true;
                Ready.TrySetResult();
                int result;
                while ((result = GetMessage(out var message, 0, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error)
            {
                Ready.TrySetException(error);
                Complete(new IOException("系统通知中断。", error));
            }
            finally
            {
                if (networkSubscribed) NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
                if (deviceNotification != 0) UnregisterDeviceNotification(deviceNotification);
                if (powerNotification != 0) UnregisterPowerSettingNotification(powerNotification);
                if (supplyNotification != 0) UnregisterPowerSettingNotification(supplyNotification);
                if (saverNotification != 0) UnregisterPowerSettingNotification(saverNotification);
                if (schemeNotification != 0) UnregisterPowerSettingNotification(schemeNotification);
                if (screenNotification != 0) UnregisterPowerSettingNotification(screenNotification);
                if (notified) WTSUnRegisterSessionNotification(Window);
                if (Window != 0) DestroyWindow(Window);
                if (registered) UnregisterClass(name, instance);
                Complete(new IOException("系统通知已停止。"));
                Stopped.TrySetResult();
            }
        }
        private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs args)
        {
            lock (Gate)
            {
                if (network == args.IsAvailable) return;
                network = args.IsAvailable; Publish(network ? "network-up" : "network-down");
            }
        }
        private nint Handle(nint window, uint message, nint w, nint l)
        {
            if (message is 0x001E or 0x001A) { ClockChanged(); return 0; } // clock/settings changes; no payload is read
            if (message == 0x0219 && w is 0x8000 or 0x8004 && l != 0)
            {
                // DEV_BROADCAST_DEVICEINTERFACE: inspect only size/type/class GUID, never its device path.
                if (Marshal.ReadInt32(l) >= Marshal.SizeOf<DeviceInterface>() && Marshal.ReadInt32(l, 4) == 5 && Marshal.PtrToStructure<Guid>(l + 12) == UsbDevice)
                    Publish(w == 0x8000 ? "usb-arrived" : "usb-removed");
                return 1;
            }
            if (message == 0x007E) { Publish("display-changed"); return 0; } // WM_DISPLAYCHANGE; do not retain resolution/depth payload
            if (message == 0x02B1)
            {
                if (w is 1 or 2 or 3 or 4) ScreenStateChanged(null); // console/remote attachment is not a physical screen report
                if (w == 7) Publish("locked"); else if (w == 8) Publish("unlocked");
                return 0;
            }
            if (message == 0x0218)
            {
                if (w == 0x8013 && l != 0 && Marshal.PtrToStructure<Guid>(l) == PowerSavingStatus)
                {
                    SaverStateChanged(Marshal.ReadInt32(l, 16) != sizeof(uint) || unchecked((uint)Marshal.ReadInt32(l, 20)) > 1);
                    return 1; // a report hint, never a legacy supply event or inferred low battery edge
                }
                if (w == 0x8013 && l != 0 && Marshal.PtrToStructure<Guid>(l) == ActivePowerScheme)
                {
                    // GUID payload is only a hint; the consumer queries the current active scheme.
                    SchemeStateChanged(Marshal.ReadInt32(l, 16) != 16 || Marshal.PtrToStructure<Guid>(l + 20) == Guid.Empty);
                    return 1; // never leak new notifications into existing supply or battery edges
                }
                if (w == 0x8013 && l != 0 && Marshal.PtrToStructure<Guid>(l) == SessionDisplayStatus)
                {
                    // Copy only the documented DWORD while the message owns its buffer. No IO or user content.
                    ScreenStateChanged(Marshal.ReadInt32(l, 16) == sizeof(uint) ? unchecked((uint)Marshal.ReadInt32(l, 20)) : null);
                    return 1;
                }
                if (w == 0x8013 && l != 0 && Marshal.ReadInt32(l, 16) == sizeof(uint))
                {
                    var setting = Marshal.PtrToStructure<Guid>(l);
                    if (setting == BatteryPercentage)
                    {
                        PowerStateChanged();
                        foreach (var kind in battery.Update(unchecked((uint)Marshal.ReadInt32(l, 20)))) Publish(kind);
                    }
                    else if (setting == AcDcPowerSource) PowerStateChanged();
                }
                if (w == 0x04) { ScreenStateChanged(null); SchemeStateChanged(true); SaverStateChanged(true); } // suspend invalidates cached state before possible resume notifications
                if (w == 0x12) { ScreenStateChanged(null); ClockChanged(); Publish("resumed"); } // automatic resume; do not also emit the interactive-resume broadcast
                if (w == 0x0A)
                {
                    PowerStateChanged();
                    if (GetSystemPowerStatus(out var status))
                    {
                        var before = supply; supply = status.AcLine;
                        if (before != 255 && supply != before && supply is 0 or 1) Publish(supply == 1 ? "ac" : "battery");
                    }
                }
                return 1;
            }
            if (message == 0x10) { PostQuitMessage(0); return 0; }
            return DefWindowProc(window, message, w, l);
        }
        private void ClockChanged()
        {
            lock (Gate) foreach (var client in Clients) if (client.clockOnly) client.clocks.Writer.TryWrite(1);
        }
        private void Complete(Exception error)
        {
            lock (Gate) foreach (var client in Clients) { client.failed = true; client.queue.Writer.TryComplete(error); client.clocks.Writer.TryComplete(error); }
        }
        private void Publish(string kind)
        {
            lock (Gate) foreach (var client in Clients)
                if (!client.clockOnly && !client.screenPowerOnly && (!client.displayOnly || kind is "display-changed" or "resumed") && (!client.powerOnly || kind == "resumed") && (!client.schemeOnly || kind == "resumed") && (!client.saverOnly || kind == "resumed") && (!client.usbOnly || kind is "usb-arrived" or "usb-removed" or "resumed") && !client.queue.Writer.TryWrite(kind))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("系统事件队列已满，检测状态未知。")); }
        }
        private void PowerStateChanged()
        {
            lock (Gate) foreach (var client in Clients)
                if (client.powerOnly && !client.schemeOnly && !client.saverOnly && !client.clockOnly && !client.usbOnly && !client.screenPowerOnly && !client.queue.Writer.TryWrite("power-state-changed"))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("供电状态通知队列已满，检测状态未知。")); }
        }
        private void SaverStateChanged(bool reset)
        {
            lock (Gate) foreach (var client in Clients)
                if (client.saverOnly && !client.schemeOnly && !client.clockOnly && !client.usbOnly && !client.powerOnly && !client.displayOnly && !client.screenPowerOnly &&
                    !client.queue.Writer.TryWrite(reset ? "power-saver-reset" : "power-saver-changed"))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("Windows节能通知队列已满，状态未知。")); }
        }
        private void SchemeStateChanged(bool reset)
        {
            lock (Gate) foreach (var client in Clients)
                if (client.schemeOnly && !client.saverOnly && !client.clockOnly && !client.usbOnly && !client.powerOnly && !client.displayOnly && !client.screenPowerOnly &&
                    !client.queue.Writer.TryWrite(reset ? "power-scheme-reset" : "power-scheme-changed"))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("电源方案通知队列已满，状态未知。")); }
        }
        private void ScreenStateChanged(uint? state)
        {
            var kind = state switch { 0 => "screen-off", 1 => "screen-on", 2 => "screen-dim", _ => "screen-reset" };
            lock (Gate) foreach (var client in Clients)
                if (client.screenPowerOnly && !client.schemeOnly && !client.saverOnly && !client.clockOnly && !client.usbOnly && !client.powerOnly && !client.displayOnly && !client.queue.Writer.TryWrite(kind))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("屏幕电源通知队列已满，状态未知。")); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public uint Size, DeviceType, Reserved; public Guid ClassGuid; public ushort Name; }
    [DllImport("user32.dll", EntryPoint="RegisterDeviceNotificationW", SetLastError=true)] private static extern nint RegisterDeviceNotification(nint recipient, ref DeviceInterface filter, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterDeviceNotification(nint notification);
    private delegate nint Procedure(nint window,uint message,nint w,nint l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass { public uint Size,Style; public Procedure Procedure; public int ClassExtra,WindowExtra; public nint Instance,Icon,Cursor,Background; public string? Menu,Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W,L; public uint Time; public int X,Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte AcLine,BatteryFlag,BatteryPercent,SystemStatus; public uint LifeTime,FullLifeTime; }
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint extra,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll",SetLastError=true)] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint RegisterPowerSettingNotification(nint recipient,ref Guid setting,uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("wtsapi32.dll",SetLastError=true)] private static extern bool WTSRegisterSessionNotification(nint window,uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
}
