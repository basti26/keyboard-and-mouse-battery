using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
            AppLog.Write("fatal unhandled=" + e.ExceptionObject);
        };
        try
        {
            if (Array.IndexOf(args, "--diagnose") >= 0)
            {
                AppLog.Write("diagnose start");
                BatterySnapshot snapshot = BatteryReader.Read(Hid.ReadMouse, Hid.ReadKeyboard,
                    delegate { Thread.Sleep(1500); }, delegate { return false; });
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic.txt"),
                    "Mouse=" + snapshot.Mouse + Environment.NewLine + "MouseSource=" + Hid.MouseSource +
                    Environment.NewLine + "Keyboard=" + snapshot.Keyboard, Encoding.UTF8);
                AppLog.Write("diagnose result mouse=" + snapshot.Mouse + " keyboard=" + snapshot.Keyboard + " source=" + Hid.MouseSource);
                return;
            }
            if (Array.IndexOf(args, "--install-startup") >= 0) SetStartup(true);
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\VxeKzziBattery.Native.v1", out created))
            {
                if (!created) { AppLog.Write("startup skipped: another instance is running"); return; }
                try
                {
                    AppLog.Write("app start path=" + Application.ExecutablePath + " autostart=" + (Array.IndexOf(args, "--autostart") >= 0));
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.Run(new BatteryContext());
                }
                finally { AppLog.Write("app stopped"); mutex.ReleaseMutex(); }
            }
        }
        catch (Exception e) {
            AppLog.Write("fatal main=" + e);
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
        AppLog.Write("autostart enabled=" + enable);
    }
}

internal sealed class BatteryContext : ApplicationContext
{
    private readonly Control dispatcher = new Control();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer activityTimer = new System.Windows.Forms.Timer();
    private Point lastCursor;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem mouseItem = new ToolStripMenuItem("鼠标（左红）：正在读取…");
    private readonly ToolStripMenuItem keyboardItem = new ToolStripMenuItem("键盘（右蓝）：正在读取…");
    private readonly ToolStripMenuItem sourceItem = new ToolStripMenuItem("鼠标来源：正在读取…");
    private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("开机启动");
    private readonly bool[] warned = new bool[2];
    private readonly BatteryHistory mouseHistory = new BatteryHistory();
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
        menu.Opening += delegate { Refresh("menu-open"); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即刷新", null, delegate { Refresh("manual"); });
        menu.Items.Add("打开日志文件夹", null, delegate {
            try {
                Directory.CreateDirectory(AppLog.DirectoryPath);
                Process.Start(new ProcessStartInfo { FileName = AppLog.DirectoryPath, UseShellExecute = true });
            }
            catch (Exception e) { AppLog.Write("open log folder failed=" + e); MessageBox.Show(e.Message, Program.Name); }
        });
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
        tray.DoubleClick += delegate { Refresh("double-click"); };
        tray.Visible = true;
        timer.Interval = 60 * 1000;
        timer.Tick += delegate { Refresh("timer"); };
        timer.Start();
        resumeTimer.Interval = 3000;
        resumeTimer.Tick += delegate { resumeTimer.Stop(); Refresh("resume"); };
        activityTimer.Interval = 2000;
        activityTimer.Tick += delegate {
            if (closing || Interlocked.CompareExchange(ref busy, 0, 0) != 0) return;
            Point cursor;
            if (GetCursorPos(out cursor) && cursor != lastCursor) {
                lastCursor = cursor;
                Refresh("cursor-moved-after-missing");
            }
        };
        SystemEvents.PowerModeChanged += OnPower;
        Refresh("startup");
    }
    private void Post(Action action)
    {
        if (closing) return;
        try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        AppLog.Write("power mode=" + e.Mode);
        if (e.Mode == PowerModes.Resume) Post(delegate { resumeTimer.Stop(); resumeTimer.Start(); });
    }
    private void Refresh(string reason)
    {
        if (closing || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate {
            Stopwatch watch = Stopwatch.StartNew();
            AppLog.Write("refresh start reason=" + reason);
            int? m = null, k = null;
            try {
                BatterySnapshot snapshot = BatteryReader.Read(Hid.ReadMouse, Hid.ReadKeyboard,
                    delegate { Thread.Sleep(1500); }, delegate { return closing; });
                m = snapshot.Mouse; k = snapshot.Keyboard;
            }
            catch (Exception e) { AppLog.Write("refresh error=" + e); }
            finally { Interlocked.Exchange(ref busy, 0); }
            AppLog.Write("refresh result mouse=" + Format(m) + " keyboard=" + Format(k) + " source=" + Hid.MouseSource + " elapsed_ms=" + watch.ElapsedMilliseconds);
            Post(delegate {
                mouseHistory.Observe(m, DateTimeOffset.Now);
                mouse = mouseHistory.Value; keyboard = k;
                if (m.HasValue) activityTimer.Stop();
                else if (!activityTimer.Enabled && GetCursorPos(out lastCursor)) activityTimer.Start();
                mouseItem.Text = "鼠标 VXE R1SE+（左红）：" + Format(mouse);
                if (mouseHistory.IsStale) mouseItem.Text += "（上次读取 " + mouseHistory.UpdatedAt.Value.ToString("MM-dd HH:mm:ss") + "）";
                keyboardItem.Text = "键盘 珂芝 K75（右蓝）：" + Format(k);
                sourceItem.Text = "鼠标来源：" + Hid.MouseSource;
                if (!m.HasValue) sourceItem.Text += "（" + Hid.MouseLinkStatus + "）";
                tray.Text = "鼠标(左红) " + Format(mouse) + (mouseHistory.IsStale ? "（上次 " + mouseHistory.UpdatedAt.Value.ToString("HH:mm") + "）" : "") + " / 键盘(右蓝) " + Format(k);
                PaintIcon();
                AppLog.Write("display mouse=" + Format(mouse) + " cached=" + mouseHistory.IsStale + " last_success=" + mouseHistory.UpdatedAt + " keyboard=" + Format(k));
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
        AppLog.Write("exit requested");
        closing = true;
        SystemEvents.PowerModeChanged -= OnPower;
        timer.Dispose(); resumeTimer.Dispose(); activityTimer.Dispose();
        tray.Visible = false; tray.Dispose(); menu.Dispose();
        if (drawing != null) drawing.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}

internal sealed class BatteryHistory
{
    internal int? Value { get; private set; }
    internal DateTimeOffset? UpdatedAt { get; private set; }
    internal bool IsStale { get; private set; }
    internal void Observe(int? reading, DateTimeOffset now)
    {
        if (reading.HasValue) {
            Value = reading;
            UpdatedAt = now;
            IsStale = false;
        }
        else IsStale = Value.HasValue;
    }
}

internal struct BatterySnapshot
{
    internal int? Mouse, Keyboard;
}

internal static class BatteryReader
{
    internal static BatterySnapshot Read(Func<int?> mouse, Func<int?> keyboard, Action wait, Func<bool> stopping)
    {
        BatterySnapshot result = new BatterySnapshot { Mouse = TryRead(mouse, "mouse"), Keyboard = TryRead(keyboard, "keyboard") };
        if ((result.Mouse.HasValue && result.Keyboard.HasValue) || stopping()) return result;
        // One delayed recovery pass; successful devices are not queried again.
        AppLog.Write("retry scheduled delay_ms=1500 mouse=" + !result.Mouse.HasValue + " keyboard=" + !result.Keyboard.HasValue);
        wait();
        if (stopping()) return result;
        if (!result.Mouse.HasValue) result.Mouse = TryRead(mouse, "mouse retry");
        if (!result.Keyboard.HasValue) result.Keyboard = TryRead(keyboard, "keyboard retry");
        AppLog.Write("retry finished mouse=" + (result.Mouse.HasValue ? result.Mouse + "%" : "unknown") + " keyboard=" + (result.Keyboard.HasValue ? result.Keyboard + "%" : "unknown"));
        return result;
    }
    private static int? TryRead(Func<int?> read, string name)
    {
        try { return read(); }
        catch (Exception e) { AppLog.Write(name + " exception=" + e); return null; }
    }
}

internal static class Hid
{
    private sealed class Device { internal string Path; internal ushort Vid, Pid; internal Caps Caps; }
    internal static string MouseSource = "未连接";
    internal static string MouseLinkStatus = "连接状态未知";
    internal static int? ReadMouse()
    {
        MouseLinkStatus = "连接状态未知";
        List<Device> candidates = new List<Device>();
        foreach (Device d in Enumerate())
        {
            if (d.Caps.UsagePage == 0xff02 && d.Caps.Usage == 2 &&
                ((d.Vid == 0x373b && d.Pid == 0x1085) || (d.Vid == 0x3554 && d.Pid == 0xf58f))) candidates.Add(d);
        }
        // Never switch to a different device just because the receiver timed out.
        bool receiverPresent = candidates.Exists(delegate(Device d) { return d.Vid == 0x373b; });
        MouseSource = receiverPresent ? "无线接收器 373B:1085" : candidates.Count > 0 ? "VXE 3554:F58F" : "未连接";
        AppLog.Write("mouse candidates=" + candidates.Count + " selected=" + MouseSource);
        foreach (Device d in candidates)
        {
            if (receiverPresent && d.Vid != 0x373b) continue;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (SafeFileHandle h = Open(d.Path, true))
                {
                    if (h.IsInvalid) { AppLog.Write("mouse open failed win32=" + Marshal.GetLastWin32Error()); continue; }
                    if (d.Caps.OutputReportByteLength != 17 || d.Caps.InputReportByteLength < 17) { AppLog.Write("mouse unsupported report lengths"); continue; }
                    byte[] request = new byte[17]; request[0] = 8; request[1] = 4; request[16] = 73;
                    if (Transfer(h, request, false, 500) != request.Length) continue;
                    byte[] reply = new byte[d.Caps.InputReportByteLength];
                    int count = Transfer(h, reply, true, 700);
                    int sum = 0;
                    if (count == 17) for (int i = 0; i < 17; i++) sum += reply[i];
                    AppLog.Write("mouse reply attempt=" + (attempt + 1) + " count=" + count + " checksum=" + (sum & 255).ToString("X2") + " bytes=" + (count > 0 ? BitConverter.ToString(reply, 0, count) : "none"));
                    int percent;
                    if (MouseProtocol.TryValue(reply, count, 4, 100, out percent)) return percent;
                }
            }
            // Read-only receiver status; distinguishes unavailable telemetry from an IO timeout.
            using (SafeFileHandle h = Open(d.Path, true))
            {
                if (h.IsInvalid) continue;
                byte[] request = new byte[17]; request[0] = 8; request[1] = 3; request[16] = 74;
                if (Transfer(h, request, false, 500) != 17) continue;
                byte[] reply = new byte[d.Caps.InputReportByteLength];
                int count = Transfer(h, reply, true, 700), available;
                if (MouseProtocol.TryValue(reply, count, 3, 1, out available)) {
                    MouseLinkStatus = available == 1 ? "接收器报告鼠标可用" : "接收器报告鼠标不可用";
                    AppLog.Write("mouse receiver availability=" + available + " bytes=" + BitConverter.ToString(reply, 0, count));
                }
                else AppLog.Write("mouse receiver status invalid count=" + count + " bytes=" + (count > 0 ? BitConverter.ToString(reply, 0, count) : "none"));
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
                if (h.IsInvalid) { AppLog.Write("keyboard open failed win32=" + Marshal.GetLastWin32Error()); continue; }
                byte[] reply = new byte[d.Caps.FeatureReportByteLength]; reply[0] = 6;
                bool ok = HidD_GetFeature(h, reply, reply.Length);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                AppLog.Write("keyboard feature ok=" + ok + " win32=" + error + " bytes=" + (ok ? BitConverter.ToString(reply) : "none"));
                if (ok && reply[0] == 6 && reply[3] <= 100) return reply[3];
            }
        }
        AppLog.Write("keyboard no valid reading");
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
            if (ev == IntPtr.Zero) { AppLog.Write("io event creation failed"); return -1; }
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Overlapped value = new Overlapped(); value.Event = ev;
            Marshal.StructureToPtr(value, ov, false);
            bool ok = read ? ReadFile(h, buffer, bytes.Length, IntPtr.Zero, ov) : WriteFile(h, buffer, bytes.Length, IntPtr.Zero, ov);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error != 997) { AppLog.Write("io " + (read ? "read" : "write") + " failed win32=" + error); return -1; }
            uint waitResult = ok ? 0 : WaitForSingleObject(ev, timeout);
            if (!ok && waitResult != 0)
            {
                AppLog.Write("io " + (read ? "read" : "write") + " wait=" + waitResult + " timeout_ms=" + timeout);
                CancelIoEx(h, ov);
                uint cancelled;
                GetOverlappedResult(h, ov, out cancelled, true);
                return -1;
            }
            uint count;
            if (!GetOverlappedResult(h, ov, out count, true)) { AppLog.Write("io completion failed win32=" + Marshal.GetLastWin32Error()); return -1; }
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
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, [In, Out] byte[] bytes, int size);
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

internal static class MouseProtocol
{
    internal static bool TryValue(byte[] reply, int count, byte command, int maximum, out int value)
    {
        value = 0;
        if (reply == null || count != 17 || reply.Length < count || reply[0] != 8 || reply[1] != command) return false;
        int sum = 0;
        for (int i = 0; i < count; i++) sum += reply[i];
        if ((sum & 255) != 0x55 || reply[6] > maximum) return false;
        value = reply[6];
        return true;
    }
}

internal static class AppLog
{
    private static readonly object Gate = new object();
    private static readonly int Pid = Process.GetCurrentProcess().Id;
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "无线键鼠电量", "logs");
    internal static void Write(string message) { WriteTo(DirectoryPath, message, 1024 * 1024); }
    // At most three files: current + two backups. Logging errors never affect monitoring.
    internal static void WriteTo(string directory, string message, int limit)
    {
        try {
            lock (Gate) {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "battery.log");
                string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " pid=" + Pid + " " +
                    message.Replace("\r", "\\r").Replace("\n", "\\n") + Environment.NewLine;
                Encoding encoding = new UTF8Encoding(false);
                if (File.Exists(file) && new FileInfo(file).Length + encoding.GetByteCount(line) > limit) {
                    string first = file + ".1", second = file + ".2";
                    if (File.Exists(second)) File.Delete(second);
                    if (File.Exists(first)) File.Move(first, second);
                    File.Move(file, first);
                }
                File.AppendAllText(file, line, encoding);
            }
        }
        catch { /* Disk full, access denied, or a concurrent diagnostic must not stop the app. */ }
    }
}
