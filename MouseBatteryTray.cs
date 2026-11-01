// MouseBatteryTray.cs - 2.4G 鼠标电量托盘程序
// 协议 (逆向自 VID_1EA7 PID_0064 接收器 Col01 厂商接口, Report ID 0xB5):
//   TX(8B): B5 01 00 00 00 00 00 00  ->  RX(8B): B5 01 01 <电量%> ...
// 编译(Windows 自带 .NET Framework csc):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+
//        /out:MouseBatteryTray.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll MouseBatteryTray.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --once is a short-lived CLI query; run it without the single-instance mutex
        // so it works even while the tray app is running.
        foreach (string a in args)
        {
            if (a == "--once")
            {
                string err, raw;
                int? pct = TrayContext.ReadBatteryPublic(out err, out raw);
                if (pct.HasValue) Console.WriteLine("鼠标电量: " + pct.Value + "%");
                else Console.WriteLine("读取失败: " + err);
                return;
            }
        }
        bool createdNew;
        using (Mutex m = new Mutex(true, "MouseBatteryTray_1EA7_0064", out createdNew))
        {
            if (!createdNew) return;
            bool log = false; int intervalSec = 60;
            foreach (string a in args)
            {
                if (a == "--log") log = true;
                else if (a.StartsWith("--interval="))
                {
                    int v;
                    if (int.TryParse(a.Substring(11), out v) && v >= 5) intervalSec = v;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Console.WriteLine("[tray] UI-EXCEPTION: " + e.Exception);
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "MouseBatteryTray.log"),
                    string.Format("{0:HH:mm:ss.fff}  UI-EXCEPTION  {1}\r\n", DateTime.Now, e.Exception)); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Console.WriteLine("[tray] FATAL: " + e.ExceptionObject);
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "MouseBatteryTray.log"),
                    string.Format("{0:HH:mm:ss.fff}  FATAL  {1}\r\n", DateTime.Now, e.ExceptionObject)); } catch { }
            };
            Application.Run(new TrayContext(log, intervalSec * 1000));
        }
    }
}

public class TrayContext : ApplicationContext
{
    const int VID = 0x1EA7, PID = 0x0064;
    const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string RunValName = "MouseBatteryTray";

    NotifyIcon ni;
    System.Windows.Forms.Timer timer;
    Form dummy;
    ToolStripMenuItem miStartup;
    string exePath, logPath;
    bool doLog;
    int lastPct = -1;
    bool lowWarned = false;
    bool firstShown = false;
    bool busy = false;
    Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>();

    public TrayContext(bool log, int intervalMs)
    {
        doLog = log;
        exePath = Application.ExecutablePath;
        // log next to the exe (works even when %TEMP% is restricted)
        try { logPath = Path.Combine(Path.GetDirectoryName(exePath), "MouseBatteryTray.log"); }
        catch { logPath = Path.Combine(Path.GetTempPath(), "MouseBatteryTray.log"); }
        Log("TrayContext ctor start, exe=" + exePath);
        int saved = LoadInterval();
        if (saved > 0) { intervalMs = saved * 1000; Log("restored saved interval " + saved + "s"); }

        dummy = new Form();
        dummy.ShowInTaskbar = false;
        dummy.WindowState = FormWindowState.Minimized;
        dummy.Visible = false;
        IntPtr forceHandle = dummy.Handle; // force handle creation for BeginInvoke

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miRefresh = new ToolStripMenuItem("立即刷新");
        miRefresh.Click += delegate { PollNow(); };

        ToolStripMenuItem miInterval = new ToolStripMenuItem("轮询间隔");
        int[] intervals = { 10, 30, 60, 120, 300 };
        int currentSec = intervalMs / 1000;
        foreach (int sec in intervals)
        {
            string label = sec >= 60 ? (sec / 60) + " 分钟" : sec + " 秒";
            ToolStripMenuItem mi = new ToolStripMenuItem(label) { Tag = sec, Checked = (sec == currentSec) };
            mi.Click += delegate
            {
                int chosen = (int)((ToolStripMenuItem)mi).Tag;
                SetInterval(chosen);
            };
            miInterval.DropDownItems.Add(mi);
        }

        miStartup = new ToolStripMenuItem("开机自启");
        miStartup.CheckOnClick = true;
        miStartup.Checked = StartupExists();
        miStartup.Click += delegate { ToggleStartup(miStartup.Checked); };
        ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
        miExit.Click += delegate
        {
            timer.Stop();
            ni.Visible = false;
            ni.Dispose();
            Application.Exit();
        };
        menu.Items.Add(miRefresh);
        menu.Items.Add(miInterval);
        menu.Items.Add(miStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        ni = new NotifyIcon();
        ni.Icon = IconFor("–", Color.Gray);
        ni.Text = "鼠标电量：读取中…";
        ni.ContextMenuStrip = menu;
        ni.Visible = true;
        ni.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (lastPct >= 0)
                    ni.ShowBalloonTip(2500, "鼠标电量",
                        string.Format("鼠标电量 {0}%（定时自动刷新）", lastPct), ToolTipIcon.Info);
                else PollNow();
            }
        };

        timer = new System.Windows.Forms.Timer();
        timer.Interval = intervalMs;
        timer.Tick += delegate { PollNow(); };
        timer.Start();
        Log("TrayContext ctor done, first poll");
        PollNow();
    }

    void SetInterval(int seconds)
    {
        if (seconds < 5) seconds = 5;
        timer.Interval = seconds * 1000;
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\MouseBatteryTray"))
            {
                k.SetValue("IntervalSec", seconds, RegistryValueKind.DWord);
            }
        }
        catch { }
        // update checkmarks
        ToolStripMenuItem parent = null;
        foreach (ToolStripItem it in ni.ContextMenuStrip.Items)
        {
            parent = it as ToolStripMenuItem;
            if (parent != null && parent.Text == "轮询间隔") break;
            parent = null;
        }
        if (parent != null)
        {
            foreach (ToolStripItem sub in parent.DropDownItems)
            {
                ToolStripMenuItem mi = sub as ToolStripMenuItem;
                if (mi != null) mi.Checked = ((int)mi.Tag == seconds);
            }
        }
        Log("interval set to " + seconds + "s");
        PollNow();
    }

    int LoadInterval()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\MouseBatteryTray"))
            {
                if (k != null)
                {
                    object v = k.GetValue("IntervalSec");
                    if (v != null)
                    {
                        int n = Convert.ToInt32(v);
                        if (n >= 5 && n <= 3600) return n;
                    }
                }
            }
        }
        catch { }
        return -1;
    }

    void PollNow()
    {
        Log("PollNow enter, busy=" + busy);
        if (busy) return;
        busy = true;
        Task.Factory.StartNew(delegate
        {
            Log("worker: reading battery");
            string err, raw;
            int? pct = ReadBatterySafe(out err, out raw);
            Log("worker: read done, pct=" + (pct.HasValue ? pct.Value.ToString() : "null") + " err=" + err);
            try
            {
                dummy.BeginInvoke((Action)delegate { busy = false; UpdateUi(pct, err, raw); });
                Log("worker: BeginInvoke posted");
            }
            catch (Exception ex) { busy = false; Log("worker: BeginInvoke failed: " + ex.Message); }
        });
    }

    static int? ReadBatterySafe(out string err, out string raw)
    {
        try { return ReadBattery(out err, out raw); }
        catch (Exception ex) { err = "异常: " + ex.Message; raw = null; return null; }
    }

    public static int? ReadBatteryPublic(out string err, out string raw)
    {
        return ReadBatterySafe(out err, out raw);
    }

    void Log(string s)
    {
        Console.WriteLine("[tray] " + s);
        if (!doLog) return;
        try { File.AppendAllText(logPath, string.Format("{0:HH:mm:ss.fff}  {1}\r\n", DateTime.Now, s)); }
        catch { }
    }

    void UpdateUi(int? pct, string err, string raw)
    {
        Log(string.Format("UpdateUi pct={0} err={1} raw={2}", pct.HasValue ? (object)pct.Value : "null", err, raw));
        if (doLog)
        {
            try
            {
                object val = pct.HasValue ? (object)(pct.Value + "%") : (object)err;
                File.AppendAllText(logPath, string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}  {2}\r\n",
                    DateTime.Now, val, raw));
            }
            catch { }
        }
        if (pct.HasValue)
        {
            lastPct = pct.Value;
            ni.Icon = IconFor(pct.Value.ToString(), ColorFor(pct.Value));
            ni.Text = string.Format("鼠标电量 {0}%", pct.Value);
            if (!firstShown)
            {
                firstShown = true;
                ni.ShowBalloonTip(5000, "鼠标电量监控已启动",
                    string.Format("当前电量 {0}%。图标在任务栏右下角 ^ 折叠区内，可将其拖出或到 设置>个性化>任务栏>其他系统托盘图标 设为始终显示。", pct.Value),
                    ToolTipIcon.Info);
            }
            if (pct.Value < 20 && !lowWarned)
            {
                lowWarned = true;
                ni.ShowBalloonTip(6000, "鼠标电量低",
                    string.Format("鼠标电量仅剩 {0}%，建议尽快充电或更换电池。", pct.Value), ToolTipIcon.Warning);
            }
            if (pct.Value >= 25) lowWarned = false;
        }
        else
        {
            ni.Icon = IconFor("–", Color.Gray);
            ni.Text = "鼠标电量：" + (err ?? "无法读取");
        }
    }

    Color ColorFor(int pct)
    {
        if (pct > 50) return Color.FromArgb(40, 165, 60);    // 绿
        if (pct > 20) return Color.FromArgb(235, 150, 20);   // 橙
        return Color.FromArgb(215, 45, 60);                  // 红
    }

    Icon IconFor(string s, Color c)
    {
        string key = s + "|" + c.ToArgb();
        Icon existing;
        if (iconCache.TryGetValue(key, out existing)) return existing;
        Bitmap bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                using (Brush br = new SolidBrush(c))
                {
                    SizeF sz = g.MeasureString(s, f);
                    g.DrawString(s, f, br, (16f - sz.Width) / 2f, (16f - sz.Height) / 2f);
                }
            }
        }
        Icon icon = Icon.FromHandle(bmp.GetHicon());
        iconCache[key] = icon;
        return icon;
    }

    bool StartupExists()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
        {
            if (k == null) return false;
            return k.GetValue(RunValName) != null;
        }
    }

    void ToggleStartup(bool enable)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (enable) k.SetValue(RunValName, "\"" + exePath + "\"");
            else
            {
                try { k.DeleteValue(RunValName, false); }
                catch { }
            }
        }
    }

    // ================= HID 厂商协议（已在 PowerShell 版本中验证可用） =================
    static Guid HidGuid = new Guid(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr dev, IntPtr devInfo, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr dev, ref SP_DEVICE_INTERFACE_DATA d, IntPtr buf, uint size, out uint req, IntPtr extra);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr dev);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, IntPtr buf, uint n, out uint read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(IntPtr h, IntPtr ov, out uint read, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(IntPtr h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr attr, bool manual, bool initial, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(IntPtr h, IntPtr buf, uint len);

    static string FindInterface()
    {
        Guid g = HidGuid;
        IntPtr dev = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (dev == IntPtr.Zero || dev == (IntPtr)(-1)) return null;
        try
        {
            uint idx = 0;
            while (true)
            {
                SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(dev, IntPtr.Zero, ref g, idx, ref d)) break;
                idx++;
                uint req;
                SetupDiGetDeviceInterfaceDetail(dev, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                if (req == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)req);
                try
                {
                    Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 6); // detail cbSize
                    if (!SetupDiGetDeviceInterfaceDetail(dev, ref d, buf, req, out req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    if (path == null) continue;
                    string lower = path.ToLowerInvariant();
                    if (lower.Contains(string.Format("vid_{0:x4}&pid_{1:x4}", VID, PID)) && lower.Contains("col01"))
                        return path;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(dev); }
        return null;
    }

    // 返回电量百分比；失败时 err 给出原因。与验证过的 PS 版本逻辑一致：
    // 未管理内存、先挂读后发命令、x64 OVERLAPPED.hEvent 在偏移 24、等待后必须冲刷 IRP 再收尾。
    static int? ReadBattery(out string err, out string raw)
    {
        err = null; raw = null;
        string path = FindInterface();
        if (path == null) { err = "未检测到接收器"; return null; }
        IntPtr h = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (h == IntPtr.Zero || h == (IntPtr)(-1)) { err = "打开设备失败"; return null; }

        IntPtr rmem = Marshal.AllocHGlobal(8);
        IntPtr txmem = Marshal.AllocHGlobal(8);
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
        IntPtr ovMem = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[] { 0xB5, 0x01, 0, 0, 0, 0, 0, 0 }, 0, txmem, 8);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                for (int i = 0; i < 32; i += 8) Marshal.WriteIntPtr(ovMem, i, IntPtr.Zero);
                Marshal.WriteIntPtr(ovMem, 24, ev);   // x64: OVERLAPPED.hEvent 位于偏移 24
                ResetEvent(ev);
                uint got;
                ReadFile(h, rmem, 8, out got, ovMem);           // 先挂异步读
                bool txOk = HidD_SetOutputReport(h, txmem, 8);  // 再发命令
                uint w = WaitForSingleObject(ev, 2500);
                CancelIoEx(h, IntPtr.Zero);                     // 冲刷 IRP 到终态
                uint got2;
                GetOverlappedResult(h, ovMem, out got2, true);
                got = got2;
                if (w == 0 && got >= 4 && txOk)
                {
                    byte[] rb = new byte[8];
                    Marshal.Copy(rmem, rb, 0, 8);
                    int n = (int)Math.Min(got, 8);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < n; i++) { sb.Append(rb[i].ToString("X2")); if (i < n - 1) sb.Append(' '); }
                    raw = sb.ToString();
                    if (rb[0] == 0xB5 && rb[1] == 0x01 && rb[3] <= 100) return rb[3];
                }
                Thread.Sleep(120);
            }
            err = "鼠标无响应（可能休眠或未开机）";
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(rmem);
            Marshal.FreeHGlobal(txmem);
            Marshal.FreeHGlobal(ovMem);
            CloseHandle(ev);
            CloseHandle(h);
        }
    }
}
