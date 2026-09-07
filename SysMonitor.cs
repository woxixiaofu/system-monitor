using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SysMonitor
{
    public class MainForm : Form
    {
        // ---------- 采集结果缓存 ----------
        private double cpuPct;
        private string cpuTempText = "--";
        private double ramUsedMB, totalRamMB, ramPct;
        private List<DiskItem> disks = new List<DiskItem>();
        private double gpuUtil, gpuTemp;
        private double gpuUsedGB, gpuTotalGB;
        private bool hasGpu;
        private string gpuName = "";
        private string nvidiaSmiPath = "";

        // ---------- 硬件型号缓存 ----------
        private string cpuModel = "";
        private List<MemStick> memSticks = new List<MemStick>();
        private Dictionary<string, string> diskModels = new Dictionary<string, string>();

        // ---------- 界面状态 ----------
        private bool topMostOn = true;
        private Rectangle rectTopBtn, rectCloseBtn;
        private bool hoverTop, hoverClose;
        private System.Windows.Forms.Timer timer;

        // ---------- 布局 ----------
        private const int START_Y = 40;
        private const int GAP = 8;
        private const int BAR_H = 40;
        private List<int> order = new List<int> { 0, 1, 2, 3 };

        // ---------- 拖动排序状态 ----------
        private int dragId = -1;
        private int dragIdx;
        private int dragStartTop;
        private int dragStartY;

        // ---------- 设置栏控件 ----------
        private Label lblRefresh;
        private TextBox txtRefresh;
        private Button btnApply;
        private Button btnReset;
        private Button btnBoost;
        private double refreshSec = 2.5;

        // ---------- 一键加速状态 ----------
        private string lastBoostResult = "";
        private bool boosting = false;

        // ---------- 配置持久化 ----------
        private string cfgPath = Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor"),
            "config.txt");

        // ---------- CPU 采样状态 ----------
        private long prevIdle, prevTotal;
        private bool cpuFirst = true;

        // ---------- 缓存字体 / 画刷 ----------
        private static Font fTitle = MakeFont(11.5F, FontStyle.Bold);
        private static Font fName = MakeFont(8.5F, FontStyle.Regular);
        private static Font fValue = MakeFont(9F, FontStyle.Bold);
        private static Font fSmall = MakeFont(7.5F, FontStyle.Regular);
        private static Font fBtn = MakeFont(8F, FontStyle.Regular);

        private static SolidBrush brText = new SolidBrush(Color.FromArgb(232, 236, 242));
        private static SolidBrush brSub = new SolidBrush(Color.FromArgb(138, 146, 158));
        private static SolidBrush brCard = new SolidBrush(Color.FromArgb(38, 42, 52));
        private static SolidBrush brTrack = new SolidBrush(Color.FromArgb(29, 32, 40));
        private static SolidBrush brBorder = new SolidBrush(Color.FromArgb(58, 63, 74));
        private static SolidBrush brAccent = new SolidBrush(Color.FromArgb(96, 168, 255));
        private static SolidBrush brGreen = new SolidBrush(Color.FromArgb(62, 207, 142));
        private static SolidBrush brYellow = new SolidBrush(Color.FromArgb(245, 166, 35));
        private static SolidBrush brRed = new SolidBrush(Color.FromArgb(241, 82, 74));
        private static SolidBrush brBtnHover = new SolidBrush(Color.FromArgb(60, 66, 80));
        private static SolidBrush brTopOn = new SolidBrush(Color.FromArgb(40, 70, 110));
        private static SolidBrush brWhite = new SolidBrush(Color.White);

        private class DiskItem
        {
            public string Name;
            public long UsedGB, TotalGB;
            public double Pct;
            public string Model = "";
        }

        private class MemStick
        {
            public string Slot;
            public string Info;
        }

        // ---------- Win32 ----------
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const int WM_NCHITTEST = 0x84;

        public MainForm()
        {
            Text = "系统监控";
            ClientSize = new Size(430, 430);
            MinimumSize = new Size(360, 430);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(22, 24, 30);
            TopMost = true;

            // 加载程序自身内嵌图标（任务栏 / 窗口显示）
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            LoadConfig();
            BuildBottomBar();
            InitCounters();
            Collect();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = (int)(refreshSec * 1000);
            timer.Tick += delegate { Collect(); Invalidate(); };
            timer.Start();

            // 启动后主动释放空闲物理内存
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 3000;
            t.Tick += delegate
            {
                t.Stop();
                try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
            };
            t.Start();
        }

        private void BuildBottomBar()
        {
            lblRefresh = new Label();
            lblRefresh.Text = "刷新(秒)";
            lblRefresh.ForeColor = Color.FromArgb(180, 186, 196);
            lblRefresh.BackColor = Color.FromArgb(22, 24, 30);
            lblRefresh.Font = new Font("Microsoft YaHei UI", 8F);
            lblRefresh.AutoSize = true;
            lblRefresh.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            txtRefresh = new TextBox();
            txtRefresh.Text = refreshSec.ToString("0.0", CultureInfo.InvariantCulture);
            txtRefresh.Width = 42;
            txtRefresh.BackColor = Color.FromArgb(38, 42, 52);
            txtRefresh.ForeColor = Color.FromArgb(232, 236, 242);
            txtRefresh.BorderStyle = BorderStyle.FixedSingle;
            txtRefresh.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            btnApply = new Button();
            btnApply.Text = "应用";
            btnApply.FlatStyle = FlatStyle.Flat;
            btnApply.FlatAppearance.BorderColor = Color.FromArgb(80, 90, 110);
            btnApply.BackColor = Color.FromArgb(40, 46, 58);
            btnApply.ForeColor = Color.FromArgb(210, 216, 226);
            btnApply.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnApply.Click += delegate { ApplyRefreshFromBox(); };

            btnReset = new Button();
            btnReset.Text = "复位";
            btnReset.FlatStyle = FlatStyle.Flat;
            btnReset.FlatAppearance.BorderColor = Color.FromArgb(80, 90, 110);
            btnReset.BackColor = Color.FromArgb(40, 46, 58);
            btnReset.ForeColor = Color.FromArgb(210, 216, 226);
            btnReset.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnReset.Click += delegate { ResetLayout(); };

            btnBoost = new Button();
            btnBoost.Text = "一键加速";
            btnBoost.FlatStyle = FlatStyle.Flat;
            btnBoost.FlatAppearance.BorderColor = Color.FromArgb(42, 168, 116);
            btnBoost.BackColor = Color.FromArgb(34, 142, 98);
            btnBoost.ForeColor = Color.White;
            btnBoost.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            btnBoost.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnBoost.Click += delegate { RunBoost(); };

            int y = ClientSize.Height - 34;
            lblRefresh.Location = new Point(16, y + 8);
            txtRefresh.Location = new Point(76, y + 4);
            btnApply.Location = new Point(126, y + 3);
            btnReset.Location = new Point(ClientSize.Width - 70, y + 3);
            btnBoost.Location = new Point(ClientSize.Width - 166, y + 3);
            btnApply.Size = new Size(58, 26);
            btnReset.Size = new Size(58, 26);
            btnBoost.Size = new Size(90, 26);

            Controls.Add(lblRefresh);
            Controls.Add(txtRefresh);
            Controls.Add(btnApply);
            Controls.Add(btnReset);
            Controls.Add(btnBoost);
        }

        // ---------- 一键加速 ----------
        private void RunBoost()
        {
            if (boosting) return;
            boosting = true;
            btnBoost.Enabled = false;
            btnBoost.Text = "加速中...";
            new System.Threading.Thread(delegate()
            {
                double freedMB = 0, tempMB = 0;
                try
                {
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            long before = p.WorkingSet64;
                            if (EmptyWorkingSet(p.Handle))
                            {
                                long after = p.WorkingSet64;
                                if (before > after) freedMB += (before - after) / (1024.0 * 1024.0);
                            }
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    tempMB = CleanTempFolders();
                }
                catch { }
                string res = string.Format("一键加速完成：释放内存 {0:0} MB · 清理临时文件 {1:0.0} MB", freedMB, tempMB);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        boosting = false;
                        btnBoost.Enabled = true;
                        btnBoost.Text = "一键加速";
                        lastBoostResult = res;
                        Collect();
                        Invalidate();
                        System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
                        t2.Interval = 10000;
                        t2.Tick += delegate { t2.Stop(); lastBoostResult = ""; Invalidate(); };
                        t2.Start();
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        private double CleanTempFolders()
        {
            double freedMB = 0;
            string[] dirs = { Path.GetTempPath(), Path.Combine(
                Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Windows"), "Temp") };
            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try
                        {
                            long sz = new FileInfo(f).Length;
                            File.Delete(f);
                            freedMB += sz / (1024.0 * 1024.0);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return freedMB;
        }

        private void ApplyRefresh()
        {
            timer.Interval = Math.Max(500, (int)(refreshSec * 1000));
        }

        private void ApplyRefreshFromBox()
        {
            double v;
            if (double.TryParse(txtRefresh.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                || double.TryParse(txtRefresh.Text, out v))
            {
                v = Math.Max(0.5, Math.Min(60.0, v));
                refreshSec = v;
                txtRefresh.Text = v.ToString("0.0", CultureInfo.InvariantCulture);
                ApplyRefresh();
                SaveConfig();
            }
            else
            {
                txtRefresh.Text = refreshSec.ToString("0.0", CultureInfo.InvariantCulture);
            }
            Invalidate();
        }

        private void ResetLayout()
        {
            order = new List<int> { 0, 1, 2, 3 };
            refreshSec = 2.5;
            txtRefresh.Text = "2.5";
            ApplyRefresh();
            SaveConfig();
            Invalidate();
        }

        // ---------- 配置持久化 ----------
        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(cfgPath)) return;
                foreach (string line in File.ReadAllLines(cfgPath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    if (k == "refresh")
                    {
                        double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) refreshSec = d;
                    }
                    else if (k == "order")
                    {
                        string[] ids = v.Split(',');
                        if (ids.Length == 4)
                        {
                            List<int> o = new List<int>();
                            foreach (string s in ids)
                            {
                                int i; if (int.TryParse(s.Trim(), out i) && i >= 0 && i <= 3) o.Add(i);
                            }
                            if (o.Count == 4) order = o;
                        }
                    }
                    else if (k == "w")
                    {
                        int i; if (int.TryParse(v, out i)) ClientSize = new Size(Math.Max(360, i), ClientSize.Height);
                    }
                    else if (k == "h")
                    {
                        int i; if (int.TryParse(v, out i)) ClientSize = new Size(ClientSize.Width, Math.Max(430, i));
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
                string ord = "";
                for (int i = 0; i < order.Count; i++)
                {
                    if (i > 0) ord += ",";
                    ord += order[i];
                }
                string[] lines = {
                    "refresh=" + refreshSec.ToString("0.0", CultureInfo.InvariantCulture),
                    "order=" + ord,
                    "w=" + ClientSize.Width,
                    "h=" + ClientSize.Height
                };
                File.WriteAllLines(cfgPath, lines);
            }
            catch { }
        }

        // ---------- 数据采集（跨系统通用，不依赖性能计数器） ----------
        private void InitCounters()
        {
            nvidiaSmiPath = FindNvidiaSmi();
            gpuName = GetGpuNameFromSmi();
            if (gpuName.Length == 0) gpuName = GetGpuNameWmi();
            hasGpu = gpuName.Length > 0;
            InitCpuModel();
            InitMemSticks();
            InitDiskModels();
        }

        // CPU 型号
        private void InitCpuModel()
        {
            cpuModel = "";
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (n.Length > 0)
                        {
                            // 去掉 " CPU @ 2.90GHz" 尾缀，缩短显示
                            int i = n.IndexOf(" CPU @");
                            cpuModel = i > 0 ? n.Substring(0, i) : n;
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        // 内存条型号（每根一根）
        private void InitMemSticks()
        {
            memSticks.Clear();
            try
            {
                int n = 0;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT DeviceLocator,Manufacturer,PartNumber,Capacity,Speed FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        n++;
                        double capGB = 0;
                        try { capGB = Convert.ToDouble(o["Capacity"]) / (1024.0 * 1024.0 * 1024.0); } catch { }
                        uint speed = 0;
                        try { speed = Convert.ToUInt32(o["Speed"]); } catch { }
                        string loc = Convert.ToString(o["DeviceLocator"]);
                        string slot = "槽" + n;
                        Match m = Regex.Match(loc, "(\\d+)\\s*$");
                        if (m.Success)
                        {
                            int sn = -1;
                            if (int.TryParse(m.Groups[1].Value, out sn)) slot = "槽" + (sn + 1);
                        }
                        string info = capGB > 0 ? capGB.ToString("0") + "GB" : "?GB";
                        if (speed > 0) info += " " + speed + "MHz";
                        string brand = MapMemBrand(Convert.ToString(o["Manufacturer"]));
                        if (brand.Length > 0) info += " " + brand;
                        else
                        {
                            string pn = Convert.ToString(o["PartNumber"]).Trim();
                            if (pn.Length > 0) info += " " + pn;
                        }
                        MemStick ms = new MemStick();
                        ms.Slot = slot;
                        ms.Info = info;
                        memSticks.Add(ms);
                    }
                }
            }
            catch { }
        }

        // 常见内存厂商 JEDEC 代码映射
        private string MapMemBrand(string code)
        {
            code = (code ?? "").Trim().ToUpperInvariant();
            switch (code)
            {
                case "80AD": return "SK海力士";
                case "80CE": return "三星";
                case "2C00":
                case "04CD": return "美光";
                case "859B": return "金士顿";
                case "014F": return "创见";
                default: return "";
            }
        }

        // 硬盘型号：盘符 -> 物理盘型号（通过 逻辑盘->分区->物理盘 索引关联，兼容无 ASSOCIATORS 的系统）
        private void InitDiskModels()
        {
            diskModels.Clear();
            try
            {
                Dictionary<int, string> physModel = new Dictionary<int, string>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Index,Model FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        try
                        {
                            int idx = Convert.ToInt32(o["Index"]);
                            string md = Convert.ToString(o["Model"]);
                            if (!physModel.ContainsKey(idx)) physModel[idx] = md;
                        }
                        catch { }
                    }
                }
                Dictionary<int, int> partToPhys = new Dictionary<int, int>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Antecedent,Dependent FROM Win32_DiskDriveToDiskPartition"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        int phys = ExtractPhysNum(Convert.ToString(o["Antecedent"]));
                        int dnum = ExtractDiskNum(Convert.ToString(o["Dependent"]));
                        if (phys >= 0 && dnum >= 0 && !partToPhys.ContainsKey(dnum)) partToPhys[dnum] = phys;
                    }
                }
                Dictionary<string, int> letterToDisk = new Dictionary<string, int>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        int dnum = ExtractDiskNum(Convert.ToString(o["Antecedent"]));
                        string letter = ExtractLetter(Convert.ToString(o["Dependent"]));
                        if (dnum >= 0 && letter.Length > 0 && !letterToDisk.ContainsKey(letter)) letterToDisk[letter] = dnum;
                    }
                }
                foreach (KeyValuePair<string, int> kv in letterToDisk)
                {
                    if (partToPhys.ContainsKey(kv.Value) && physModel.ContainsKey(partToPhys[kv.Value]))
                    {
                        string key = kv.Key + ":";
                        if (!diskModels.ContainsKey(key)) diskModels[key] = physModel[partToPhys[kv.Value]];
                    }
                }
            }
            catch { }
        }

        private int ExtractDiskNum(string s)
        {
            Match m = Regex.Match(s ?? "", "Disk #(\\d+)");
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : -1;
        }

        private int ExtractPhysNum(string s)
        {
            Match m = Regex.Match(s ?? "", "PHYSICALDRIVE(\\d+)");
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : -1;
        }

        private string ExtractLetter(string s)
        {
            Match m = Regex.Match(s ?? "", "([A-Za-z]):");
            return m.Success ? m.Groups[1].Value : "";
        }

        // 定位 nvidia-smi：系统目录 / NVIDIA 安装目录 / PATH
        private string FindNvidiaSmi()
        {
            string[] fixedPaths = {
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                @"C:\Program Files (x86)\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            };
            foreach (string p in fixedPaths)
            {
                try { if (File.Exists(p)) return p; } catch { }
            }
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(';'))
                {
                    try
                    {
                        string cand = Path.Combine(dir.Trim('"'), "nvidia-smi.exe");
                        if (File.Exists(cand)) return cand;
                    }
                    catch { }
                }
            }
            return "";
        }

        private string GetGpuNameFromSmi()
        {
            if (nvidiaSmiPath.Length == 0) return "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(nvidiaSmiPath, "--query-gpu=name --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    return s;
                }
            }
            catch { return ""; }
        }

        // 通用显卡名（任何品牌均可）：优先 NVIDIA，其次任一显卡
        private string GetGpuNameWmi()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    string fallback = "";
                    foreach (ManagementObject o in s.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (string.IsNullOrEmpty(n)) continue;
                        if (n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return n;
                        if (fallback.Length == 0) fallback = n;
                    }
                    return fallback;
                }
            }
            catch { return ""; }
        }

        // CPU 使用率：GetSystemTimes（不依赖性能计数器，Win7+ 通用）
        private double GetCpuUsage()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
            long total = kernel + user;
            double pct = 0;
            if (!cpuFirst && prevTotal > 0)
            {
                long dt = total - prevTotal;
                long di = idle - prevIdle;
                if (dt > 0) pct = Math.Max(0, Math.Min(100, (1.0 - (double)di / dt) * 100.0));
            }
            cpuFirst = false;
            prevIdle = idle;
            prevTotal = total;
            return pct;
        }

        // CPU 温度（部分主板不支持时显示 --）
        private string GetCpuTemp()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        double kelvin = Convert.ToDouble(o["CurrentTemperature"]) / 10.0;
                        double c = kelvin - 273.15;
                        if (c > -50 && c < 150) return c.ToString("0") + "°C";
                        break;
                    }
                }
            }
            catch { }
            return "--";
        }

        private void Collect()
        {
            try
            {
                cpuPct = GetCpuUsage();
                cpuTempText = GetCpuTemp();

                MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref ms))
                {
                    totalRamMB = (double)(ms.ullTotalPhys / (1024 * 1024));
                    double availMB = (double)(ms.ullAvailPhys / (1024 * 1024));
                    ramUsedMB = totalRamMB - availMB;
                    ramPct = totalRamMB > 0 ? ramUsedMB / totalRamMB * 100.0 : 0;
                }

                disks.Clear();
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        long total = d.TotalSize / (1024L * 1024 * 1024);
                        long free = d.AvailableFreeSpace / (1024L * 1024 * 1024);
                        long used = total - free;
                        double pct = total > 0 ? (double)used / total * 100.0 : 0;
                        DiskItem it = new DiskItem();
                        it.Name = d.Name.Substring(0, 1) + ":";
                        it.UsedGB = used; it.TotalGB = total; it.Pct = pct;
                        if (diskModels.ContainsKey(it.Name)) it.Model = diskModels[it.Name];
                        disks.Add(it);
                    }
                }
                catch { }

                CollectGpu();
                EnsureHeight();
            }
            catch { }
        }

        // 内容变多时自动拉高窗口，保证所有卡片完整可见（不压缩用户手动拉大的窗口）
        private void EnsureHeight()
        {
            try
            {
                int need = ContentHeight() + 12;
                if (ClientSize.Height < need)
                {
                    int maxH = SystemInformation.WorkingArea.Height - 40;
                    if (need > maxH) need = maxH;
                    ClientSize = new Size(ClientSize.Width, need);
                    Invalidate();
                }
            }
            catch { }
        }

        private void CollectGpu()
        {
            if (nvidiaSmiPath.Length == 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(nvidiaSmiPath,
                    "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                string o = "";
                using (Process p = Process.Start(psi))
                {
                    o = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                }
                if (o.Length > 0)
                {
                    string[] f = o.Split(',');
                    if (f.Length >= 4)
                    {
                        gpuUtil = double.Parse(f[0].Trim());
                        gpuTemp = double.Parse(f[1].Trim());
                        gpuUsedGB = double.Parse(f[2].Trim()) / 1024.0;
                        gpuTotalGB = double.Parse(f[3].Trim()) / 1024.0;
                    }
                }
            }
            catch { }
        }

        // ---------- 布局计算 ----------
        // 卡片高度随内容动态变化（内存/硬盘条目多时自动增高）
        private int CardHeight(int id)
        {
            if (id == 0) return 80;                        // CPU: 标题+进度条+型号行
            if (id == 1) { int n = Math.Min(memSticks.Count, 3); return 54 + 18 * n; }  // 内存: 标题+条+每根内存条
            if (id == 2) { int n = Math.Min(disks.Count, 5); return 32 + 30 * n; }      // 硬盘: 标题+每盘两行(用量+型号)，行距30px防重叠
            if (id == 3) return 62;                        // 显卡: 名称+数值+条
            return 58;
        }

        private int ContentHeight()
        {
            int y = START_Y;
            for (int i = 0; i < order.Count; i++) y += CardHeight(order[i]) + GAP;
            return y + BAR_H;
        }

        private int SlotTop(int idx)
        {
            int y = START_Y;
            for (int i = 0; i < idx; i++) y += CardHeight(order[i]) + GAP;
            return y;
        }

        private Rectangle CardRect(int idx)
        {
            int w = ClientSize.Width;
            return new Rectangle(14, SlotTop(idx), w - 28, CardHeight(order[idx]));
        }

        private int CardAtPoint(Point p)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (CardRect(i).Contains(p)) return i;
            }
            return -1;
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            g.Clear(Color.FromArgb(22, 24, 30));

            // 标题栏
            g.DrawString("系统监控", fTitle, brText, 16, 10);

            rectTopBtn = new Rectangle(w - 80, 8, 36, 24);
            rectCloseBtn = new Rectangle(w - 40, 8, 26, 24);
            DrawIconButton(g, rectTopBtn, hoverTop, "置顶", topMostOn ? brTopOn : brCard, topMostOn ? brAccent : brSub);
            DrawIconButton(g, rectCloseBtn, hoverClose, "✕", hoverClose ? brRed : brCard, brWhite);

            // 卡片
            for (int i = 0; i < order.Count; i++)
            {
                DrawCardContent(g, i, CardRect(i));
            }

            // 底部状态 / 提示
            if (!string.IsNullOrEmpty(lastBoostResult))
                DrawFit(g, lastBoostResult, fSmall, brGreen, new RectangleF(14, h - BAR_H - 18, w - 28, 16), StringAlignment.Near);
            else
            {
                string note = "拖动卡片可调整位置 · 拖拽窗口边缘可缩放 · CPU/硬盘温度仅供参考";
                g.DrawString(note, fSmall, brSub, 14, h - BAR_H - 18);
            }
        }

        private void DrawCardContent(Graphics g, int idx, Rectangle r)
        {
            DrawCard(g, r, dragId >= 0 && order[idx] == dragId);
            int id = order[idx];
            int cw = r.Width;
            int right = r.Right;

            if (id == 0) // CPU
            {
                g.DrawString("CPU 使用率", fName, brSub, 24, r.Y + 9);
                string val = cpuPct.ToString("0") + "%    " + cpuTempText + "    " + Environment.ProcessorCount + " 核";
                DrawFit(g, val, fValue, brText, new RectangleF(110, r.Y + 6, right - 110 - 8, 18), StringAlignment.Far);
                DrawBar(g, new Rectangle(24, r.Y + 30, cw - 52, 14), cpuPct);
                // 型号行
                if (cpuModel.Length > 0)
                    DrawFit(g, cpuModel, fSmall, brSub, new RectangleF(24, r.Y + 53, cw - 48, 16), StringAlignment.Near);
            }
            else if (id == 1) // 内存
            {
                g.DrawString("内存", fName, brSub, 24, r.Y + 9);
                string val = (ramUsedMB / 1024.0).ToString("0.0") + " / " + (totalRamMB / 1024.0).ToString("0.0")
                    + " GB    " + ramPct.ToString("0") + "%";
                DrawFit(g, val, fValue, brText, new RectangleF(110, r.Y + 6, right - 110 - 8, 18), StringAlignment.Far);
                DrawBar(g, new Rectangle(24, r.Y + 30, cw - 52, 14), ramPct);
                // 每根内存条型号
                int sy = r.Y + 52;
                for (int k = 0; k < memSticks.Count && k < 3; k++)
                {
                    MemStick ms = memSticks[k];
                    string line = ms.Slot + "  " + ms.Info;
                    DrawFit(g, line, fSmall, brSub, new RectangleF(24, sy + k * 18, cw - 48, 16), StringAlignment.Near);
                }
            }
            else if (id == 2) // 硬盘
            {
                g.DrawString("硬盘", fName, brSub, 24, r.Y + 8);
                int barW = cw - 162;
                if (barW < 60) barW = 60;
                int rowTop = r.Y + 28;
                for (int k = 0; k < disks.Count && k < 5; k++)
                {
                    DiskItem d = disks[k];
                    if (rowTop > r.Bottom - 16) break;
                    g.DrawString(d.Name, fName, brSub, 24, rowTop);
                    DrawBar(g, new Rectangle(60, rowTop - 1, barW, 12), d.Pct);
                    string dv = d.UsedGB + " / " + d.TotalGB + " GB  " + d.Pct.ToString("0") + "%";
                    DrawFit(g, dv, fValue, brText, new RectangleF(right - 116, rowTop - 5, 108, 18), StringAlignment.Far);
                    // 硬盘型号行
                    if (d.Model.Length > 0)
                        DrawFit(g, d.Model, fSmall, brSub, new RectangleF(24, rowTop + 15, cw - 48, 13), StringAlignment.Near);
                    rowTop += 30;
                }
            }
            else if (id == 3) // 显卡
            {
                g.DrawString("显卡", fName, brSub, 24, r.Y + 9);
                g.DrawString(hasGpu ? gpuName : "未检测到显卡", fName, brSub, new RectangleF(66, r.Y + 9, right - 66 - 14, 18),
                    new StringFormat { Alignment = StringAlignment.Far });
                if (hasGpu)
                {
                    if (nvidiaSmiPath.Length > 0)
                    {
                        string gv = gpuUtil.ToString("0") + "%  " + gpuTemp.ToString("0") + "°C  显存 "
                            + gpuUsedGB.ToString("0.0") + "/" + gpuTotalGB.ToString("0.0") + "GB";
                        DrawFit(g, gv, fValue, brText, new RectangleF(110, r.Y + 32, right - 110 - 8, 16), StringAlignment.Far);
                        DrawBar(g, new Rectangle(24, r.Y + 49, cw - 52, 11), gpuUtil);
                    }
                    else
                    {
                        DrawFit(g, "实时负载/温度需 NVIDIA 显卡驱动 (nvidia-smi)", fSmall, brSub,
                            new RectangleF(66, r.Y + 32, right - 66 - 14, 16), StringAlignment.Far);
                    }
                }
            }
        }

        // 自适应字号绘制：文本超出矩形宽度时自动缩小字号，保证不截断
        private void DrawFit(Graphics g, string text, Font font, Brush brush, RectangleF rect, StringAlignment align)
        {
            float size = font.Size;
            Font use = font;
            SizeF m = g.MeasureString(text, use);
            while (m.Width > rect.Width + 1 && size > 6.5f)
            {
                size -= 0.5f;
                if (!ReferenceEquals(use, font)) use.Dispose();
                use = new Font(font.FontFamily, size, font.Style);
                m = g.MeasureString(text, use);
            }
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, use, brush, rect, sf);
            }
            if (!ReferenceEquals(use, font)) use.Dispose();
        }

        private void DrawCard(Graphics g, Rectangle r, bool highlight)
        {
            using (GraphicsPath p = RoundRect(r, 10))
            {
                g.FillPath(brCard, p);
                using (Pen pen = new Pen(highlight ? brAccent : brBorder)) g.DrawPath(pen, p);
            }
        }

        private void DrawBar(Graphics g, Rectangle r, double pct)
        {
            using (GraphicsPath p1 = RoundRect(r, 7)) g.FillPath(brTrack, p1);
            if (pct > 0.5)
            {
                int bw = (int)(r.Width * Math.Min(pct, 100.0) / 100.0);
                if (bw < 4) bw = 4;
                if (bw > r.Width) bw = r.Width;
                Rectangle fr = new Rectangle(r.X, r.Y, bw, r.Height);
                SolidBrush col = pct >= 85 ? brRed : (pct >= 60 ? brYellow : brGreen);
                using (GraphicsPath p2 = RoundRect(fr, 7)) g.FillPath(col, p2);
            }
        }

        private void DrawIconButton(Graphics g, Rectangle r, bool hover, string text, Brush bg, Brush fg)
        {
            using (GraphicsPath p = RoundRect(r, 6))
            {
                g.FillPath(hover ? brBtnHover : bg, p);
                using (Pen pen = new Pen(hover ? brAccent : brBorder)) g.DrawPath(pen, p);
            }
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, fBtn, fg, r, sf);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Font MakeFont(float size, FontStyle style)
        {
            string[] names = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" };
            foreach (string n in names)
            {
                try
                {
                    Font f = new Font(n, size, style);
                    if (f.Name == n) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font("Arial", size, style);
        }

        // ---------- 交互 ----------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragId >= 0)
            {
                int curTop = dragStartTop + (e.Y - dragStartY);
                int myCenter = curTop + CardHeight(dragId) / 2;
                List<int> rest = new List<int>(order);
                rest.Remove(dragId);
                int ins = 0;
                for (int i = 0; i < rest.Count; i++)
                {
                    int center = SlotTopOf(rest, i) + CardHeight(rest[i]) / 2;
                    if (myCenter < center) { ins = i; break; }
                    ins = i + 1;
                }
                int curIdx = order.IndexOf(dragId);
                if (ins != curIdx)
                {
                    order.RemoveAt(curIdx);
                    order.Insert(ins, dragId);
                    dragStartTop = SlotTop(ins);
                    dragStartY = e.Y;
                }
                Invalidate();
                return;
            }

            bool hT = rectTopBtn.Contains(e.Location);
            bool hC = rectCloseBtn.Contains(e.Location);
            if (hT != hoverTop || hC != hoverClose)
            {
                hoverTop = hT; hoverClose = hC;
                Cursor = (hT || hC) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private int SlotTopOf(List<int> list, int idx)
        {
            int y = START_Y;
            for (int i = 0; i < idx; i++) y += CardHeight(list[i]) + GAP;
            return y;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (rectCloseBtn.Contains(e.Location)) { SaveConfig(); Close(); return; }
            if (rectTopBtn.Contains(e.Location))
            {
                topMostOn = !topMostOn;
                TopMost = topMostOn;
                Invalidate();
                return;
            }
            if (e.Y < START_Y)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                return;
            }
            if (e.Y < ClientSize.Height - BAR_H)
            {
                int idx = CardAtPoint(e.Location);
                if (idx >= 0)
                {
                    dragId = order[idx];
                    dragIdx = idx;
                    dragStartTop = SlotTop(idx);
                    dragStartY = e.Y;
                    Capture = true;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragId >= 0)
            {
                dragId = -1;
                Capture = false;
                SaveConfig();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverTop || hoverClose)
            {
                hoverTop = false; hoverClose = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();
            base.OnFormClosing(e);
        }

        // ---------- 无边框窗口缩放 ----------
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(x, y));
                int cw = ClientSize.Width, ch = ClientSize.Height;
                const int SZ = 6;
                bool l = p.X <= SZ, r = p.X >= cw - SZ, t = p.Y <= SZ, b = p.Y >= ch - SZ;
                if (l && t) { m.Result = (IntPtr)13; return; }
                if (r && t) { m.Result = (IntPtr)14; return; }
                if (l && b) { m.Result = (IntPtr)16; return; }
                if (r && b) { m.Result = (IntPtr)17; return; }
                if (l) { m.Result = (IntPtr)10; return; }
                if (r) { m.Result = (IntPtr)11; return; }
                if (t) { m.Result = (IntPtr)12; return; }
                if (b) { m.Result = (IntPtr)15; return; }
            }
            base.WndProc(ref m);
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
