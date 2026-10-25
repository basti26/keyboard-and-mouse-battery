using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    internal const string Name = "无线键鼠电量";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunName = "VxeKzziBattery";
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (Array.IndexOf(args, "--diagnose") >= 0)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic.txt"),
                    "Mouse=" + Hid.ReadMouse() + Environment.NewLine + "MouseSource=" + Hid.MouseSource +
                    Environment.NewLine + "Keyboard=" + Hid.ReadKeyboard(), Encoding.UTF8);
                return;
            }
            if (Array.IndexOf(args, "--install-startup") >= 0) SetStartup(true);
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\VxeKzziBattery.Native.v1", out created))
            {
                if (!created) return;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new BatteryContext());
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception e) {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.txt"), e.ToString(), Encoding.UTF8); } catch { }
            MessageBox.Show(e.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    internal static bool StartupEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && String.Equals(k.GetValue(RunName) as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }
    private static string StartupCommand() { return "\"" + Application.ExecutablePath + "\" --autostart"; }
    internal static void SetStartup(bool enable)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enable) k.SetValue(RunName, StartupCommand());
            else k.DeleteValue(RunName, false);
        }
    }
}

internal sealed class BatteryContext : ApplicationContext
{
    private readonly Control dispatcher = new Control();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem mouseItem = new ToolStripMenuItem("鼠标（左红）：正在读取…");
    private readonly ToolStripMenuItem keyboardItem = new ToolStripMenuItem("键盘（右蓝）：正在读取…");
    private readonly ToolStripMenuItem sourceItem = new ToolStripMenuItem("鼠标来源：正在读取…");
    private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("开机启动");
    private readonly bool[] warned = new bool[2];
    private Icon drawing;
    private int? mouse, keyboard;
    private int busy;
    private volatile bool closing;
    internal BatteryContext()
    {
        IntPtr handle = dispatcher.Handle;
        mouseItem.Enabled = keyboardItem.Enabled = sourceItem.Enabled = false;
        menu.Items.Add(mouseItem); menu.Items.Add(keyboardItem);
        menu.Items.Add(sourceItem);
        menu.Opening += delegate { Refresh(); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即刷新", null, delegate { Refresh(); });
        startupItem.Checked = Program.StartupEnabled();
        startupItem.Click += delegate {
            try { Program.SetStartup(!Program.StartupEnabled()); startupItem.Checked = Program.StartupEnabled(); }
            catch (Exception e) { MessageBox.Show("设置开机启动失败：" + e.Message, Program.Name); }
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;
        tray.Text = Program.Name + "：正在读取";
        PaintIcon();
        tray.DoubleClick += delegate { Refresh(); };
        tray.Visible = true;
        timer.Interval = 60 * 1000;
        timer.Tick += delegate { Refresh(); };
        timer.Start();
        resumeTimer.Interval = 3000;
        resumeTimer.Tick += delegate { resumeTimer.Stop(); Refresh(); };
        SystemEvents.PowerModeChanged += OnPower;
        Refresh();
    }
    private void Post(Action action)
    {
        if (closing) return;
        try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Post(delegate { resumeTimer.Stop(); resumeTimer.Start(); });
    }
    private void Refresh()
    {
        if (closing || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate {
            int? m = null, k = null;
            try { m = Hid.ReadMouse(); k = Hid.ReadKeyboard(); }
            catch { /* An unavailable device is shown as unknown, never as a cached percentage. */ }
            finally { Interlocked.Exchange(ref busy, 0); }
            Post(delegate {
                mouse = m; keyboard = k;
                mouseItem.Text = "鼠标 VXE R1SE+（左红）：" + Format(m);
                keyboardItem.Text = "键盘 珂芝 K75（右蓝）：" + Format(k);
                sourceItem.Text = "鼠标来源：" + Hid.MouseSource;
                tray.Text = "鼠标(左红) " + Format(m) + " / 键盘(右蓝) " + Format(k);
                PaintIcon();
                Low(m, 0, "鼠标 VXE R1SE+"); Low(k, 1, "键盘 珂芝 K75");
            });
        });
    }
    private static string Format(int? value) { return value.HasValue ? value + "%" : "未读取"; }
    private void Low(int? value, int index, string name)
    {
        if (!value.HasValue) return;
        if (value > 10) { warned[index] = false; return; }
        if (warned[index]) return;
        warned[index] = true;
        tray.ShowBalloonTip(8000, Program.Name, name + " 电量仅剩 " + value + "%", ToolTipIcon.Warning);
    }
    private void PaintIcon()
    {
        using (Bitmap bitmap = BuildBitmap(mouse, keyboard))
        {
            IntPtr h = bitmap.GetHicon();
            Icon next;
            try { using (Icon borrowed = Icon.FromHandle(h)) next = (Icon)borrowed.Clone(); }
            finally { DestroyIcon(h); }
            Icon old = drawing;
            drawing = next; tray.Icon = drawing;
            if (old != null) old.Dispose();
        }
    }
    internal static Bitmap BuildBitmap(int? mouse, int? keyboard)
    {
        Bitmap bitmap = new Bitmap(64, 64);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Mirrored halves start at the top; small gaps keep the devices distinct.
            HalfRing(g, mouse, Color.FromArgb(244, 75, 86), -94, -172);
            HalfRing(g, keyboard, Color.FromArgb(48, 154, 255), -86, 172);
        }
        return bitmap;
    }
    private static void HalfRing(Graphics g, int? value, Color color, float start, float sweep)
    {
        RectangleF bounds = new RectangleF(8, 8, 48, 48);
        using (Pen track = new Pen(Color.FromArgb(72, color), 9)) g.DrawArc(track, bounds, start, sweep);
        if (!value.HasValue) return;
        int percent = Math.Max(0, Math.Min(100, value.Value));
        if (percent > 0) using (Pen arc = new Pen(color, 9)) {
            g.DrawArc(arc, bounds, start, sweep * percent / 100f);
        }
    }
    protected override void ExitThreadCore()
    {
        closing = true;
        SystemEvents.PowerModeChanged -= OnPower;
        timer.Dispose(); resumeTimer.Dispose();
        tray.Visible = false; tray.Dispose(); menu.Dispose();
        if (drawing != null) drawing.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

internal static class Hid
{
    private sealed class Device { internal string Path; internal ushort Vid, Pid; internal Caps Caps; }
    internal static string MouseSource = "未连接";
    internal static int? ReadMouse()
    {
        List<Device> candidates = new List<Device>();
        foreach (Device d in Enumerate())
        {
            if (d.Caps.UsagePage == 0xff02 && d.Caps.Usage == 2 &&
                ((d.Vid == 0x373b && d.Pid == 0x1085) || (d.Vid == 0x3554 && d.Pid == 0xf58f))) candidates.Add(d);
        }
        // Never switch to a different device just because the receiver timed out.
        bool receiverPresent = candidates.Exists(delegate(Device d) { return d.Vid == 0x373b; });
        MouseSource = receiverPresent ? "无线接收器 373B:1085" : candidates.Count > 0 ? "VXE 3554:F58F" : "未连接";
        foreach (Device d in candidates)
        {
            if (receiverPresent && d.Vid != 0x373b) continue;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (SafeFileHandle h = Open(d.Path, true))
                {
                    if (h.IsInvalid || d.Caps.OutputReportByteLength != 17 || d.Caps.InputReportByteLength < 17) continue;
                    byte[] request = new byte[17]; request[0] = 8; request[1] = 4; request[16] = 73;
                    if (Transfer(h, request, false, 500) != request.Length) continue;
                    byte[] reply = new byte[d.Caps.InputReportByteLength];
                    int count = Transfer(h, reply, true, 700);
                    int sum = 0;
                    if (count == 17) for (int i = 0; i < 17; i++) sum += reply[i];
                    if (count == 17 && reply[0] == 8 && reply[1] == 4 && reply[6] <= 100 && (sum & 255) == 0x55) return reply[6];
                }
            }
        }
        return null;
    }
    internal static int? ReadKeyboard()
    {
        foreach (Device d in Enumerate())
        {
            if (d.Vid != 0x3554 || d.Pid != 0xfa09 || d.Caps.UsagePage != 0xff04 || d.Caps.FeatureReportByteLength < 8) continue;
            using (SafeFileHandle h = Open(d.Path, false))
            {
                if (h.IsInvalid) continue;
                byte[] reply = new byte[d.Caps.FeatureReportByteLength]; reply[0] = 6;
                if (HidD_GetFeature(h, reply, reply.Length) && reply[0] == 6 && reply[3] <= 100) return reply[3];
            }
        }
        return null;
    }
    private static SafeFileHandle Open(string path, bool overlapped)
    { return CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero); }
    private static IEnumerable<Device> Enumerate()
    {
        List<Device> devices = new List<Device>();
        Guid guid; HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return devices;
        try
        {
            for (uint i = 0; ; i++)
            {
                Interface data = new Interface(); data.Size = Marshal.SizeOf(typeof(Interface));
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                uint needed;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                if (needed < 6) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    // Only open the three device IDs this app supports.
                    string p = path.ToLowerInvariant();
                    if (!(p.Contains("vid_373b&pid_1085") || p.Contains("vid_3554&pid_f58f") || p.Contains("vid_3554&pid_fa09"))) continue;
                    using (SafeFileHandle h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        Attributes a = new Attributes(); a.Size = Marshal.SizeOf(typeof(Attributes));
                        if (!HidD_GetAttributes(h, ref a)) continue;
                        IntPtr preparsed;
                        if (!HidD_GetPreparsedData(h, out preparsed)) continue;
                        try {
                            Caps c;
                            if (HidP_GetCaps(preparsed, out c) == 0x110000)
                                devices.Add(new Device { Path = path, Vid = a.Vid, Pid = a.Pid, Caps = c });
                        }
                        finally { HidD_FreePreparsedData(preparsed); }
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }
    // Buffers remain allocated until overlapped IO completes, including cancellation.
    private static int Transfer(SafeFileHandle h, byte[] bytes, bool read, uint timeout)
    {
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
        IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            if (ev == IntPtr.Zero) return -1;
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Overlapped value = new Overlapped(); value.Event = ev;
            Marshal.StructureToPtr(value, ov, false);
            bool ok = read ? ReadFile(h, buffer, bytes.Length, IntPtr.Zero, ov) : WriteFile(h, buffer, bytes.Length, IntPtr.Zero, ov);
            if (!ok && Marshal.GetLastWin32Error() != 997) return -1;
            if (!ok && WaitForSingleObject(ev, timeout) != 0)
            {
                CancelIoEx(h, ov);
                uint cancelled;
                GetOverlappedResult(h, ov, out cancelled, true);
                return -1;
            }
            uint count;
            if (!GetOverlappedResult(h, ov, out count, true)) return -1;
            if (read) Marshal.Copy(buffer, bytes, 0, (int)count);
            return (int)count;
        }
        finally { if (ev != IntPtr.Zero) CloseHandle(ev); Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Interface { internal int Size; internal Guid Guid; internal uint Flags; internal UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { internal int Size; internal ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Overlapped { internal UIntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps {
        internal ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, [In, Out] byte[] bytes, int size);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref Interface data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref Interface data, IntPtr detail, uint size, out uint needed, IntPtr device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int count, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int count, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint transferred, bool wait);
    [DllImport("kernel32.dll")] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr security, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
