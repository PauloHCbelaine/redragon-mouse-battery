// Monitor de bateria na bandeja do Windows para mouses Redragon com chip Compx (VID 0x3554),
// testado no M916 PRO 1K. Só LÊ informações; nunca altera nenhuma configuração do mouse.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    // Todos os caminhos HID cujo nome contém todas as partes pedidas (ex.: "vid_3554", "col05").
    public static List<string> FindPaths(params string[] parts)
    {
        var result = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | INTERFACEDEVICE
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                int req;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                    {
                        string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)).ToLowerInvariant();
                        bool all = true;
                        foreach (string part in parts) if (!p.Contains(part)) { all = false; break; }
                        if (all) result.Add(p);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static SafeFileHandle Open(string path)
    {
        return CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // RW, share RW, OPEN_EXISTING, OVERLAPPED
    }
}

// ======================= Mouse Redragon (Compx) =======================

// O driver oficial da Redragon pode estar usando o mesmo canal ao mesmo tempo, então
// respostas podem se perder: cada pedido é repetido algumas vezes.
class Mouse : IDisposable
{
    FileStream fs;
    public Mouse(SafeFileHandle h) { fs = new FileStream(h, FileAccess.ReadWrite, 17, true); }
    public void Dispose() { fs.Dispose(); }

    // cmd 0x04 = bateria; cmd 0x08 = ler `len` bytes da memória de configuração em `addr`.
    public byte[] Query(byte cmd, int addr, int len)
    {
        var req = new byte[17];
        req[0] = 0x08; req[1] = cmd; req[3] = (byte)(addr >> 8); req[4] = (byte)addr; req[5] = (byte)len;
        int sum = 0; for (int i = 0; i < 16; i++) sum += req[i];
        req[16] = (byte)((0x55 - sum) & 0xFF);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            fs.Write(req, 0, 17);
            DateTime end = DateTime.Now.AddMilliseconds(400);
            while (DateTime.Now < end)
            {
                var buf = new byte[17];
                IAsyncResult ar = fs.BeginRead(buf, 0, 17, null, null);
                TimeSpan left = end - DateTime.Now;
                if (left < TimeSpan.Zero || !ar.AsyncWaitHandle.WaitOne(left)) return null; // fechar o handle cancela a leitura
                int n = fs.EndRead(ar);
                if (n >= 17 && buf[0] == 0x08 && buf[1] == cmd && buf[2] == 0 &&
                    (cmd != 0x08 || (buf[3] == req[3] && buf[4] == req[4])))
                    return buf;
            }
        }
        return null;
    }

    public static MouseReading Read()
    {
        var paths = Hid.FindPaths("vid_3554", "mi_01", "col05");
        // Dongle (2.4G) primeiro, cabo depois.
        paths.Sort((a, b) => (b.Contains("pid_f5d5") ? 1 : 0) - (a.Contains("pid_f5d5") ? 1 : 0));
        foreach (string p in paths)
        {
            try
            {
                using (var h = Hid.Open(p))
                {
                    if (h.IsInvalid) continue;
                    using (var m = new Mouse(h))
                    {
                        byte[] b = m.Query(0x04, 0, 0);
                        if (b == null || b[6] == 0 || b[6] > 100) continue;
                        var r = new MouseReading();
                        r.Ok = true; r.Percent = b[6]; r.Charging = b[7] != 0;
                        r.MilliVolts = (b[8] << 8) | b[9];
                        r.Wired = !p.Contains("pid_f5d5");

                        // Memória 0x0000: [polling, chk, nº de níveis de DPI, chk, nível ativo (0..), chk]
                        byte[] c = m.Query(0x08, 0x0000, 6);
                        if (c != null)
                        {
                            r.PollingHz = PollingHz(c[6]);
                            if (c[8] >= 1 && c[8] <= 8 && c[10] < c[8]) { r.DpiLevels = c[8]; r.DpiLevel = c[10] + 1; }
                        }
                        return r;
                    }
                }
            }
            catch { }
        }
        return new MouseReading();
    }

    static int PollingHz(byte v)
    {
        switch (v)
        {
            case 0x08: return 125; case 0x04: return 250; case 0x02: return 500; case 0x01: return 1000;
            case 0x10: return 2000; case 0x20: return 4000; case 0x40: return 8000; default: return 0;
        }
    }
}

class MouseReading
{
    // Percent é o que o mouse informa. Enquanto carrega, a voltagem sobe e esse valor fica
    // inflado, então o app não o mostra como nível real nesse período.
    public bool Ok; public int Percent; public bool Charging; public int MilliVolts; public bool Wired;
    public int PollingHz;
    public int DpiLevel, DpiLevels;
    public DateTime Time = DateTime.Now;
}

// ======================= Dispositivos Bluetooth =======================

// O Windows guarda a bateria dos dispositivos Bluetooth LE (teclados, controles, fones…) como
// propriedades do próprio dispositivo. Basta ler, sem falar com o aparelho.
class BtDevice
{
    public string Id, Name, Category; public int Percent; public bool Charging; public DateTime Updated;
}

static class Bluetooth
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int req, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    static DEVPROPKEY KBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY KCharging = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 3);
    static DEVPROPKEY KUpdated = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 7);
    static DEVPROPKEY KName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);     // FriendlyName
    static DEVPROPKEY KInstance = new DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 256); // InstanceId
    static DEVPROPKEY KCategory = new DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);  // DeviceContainer_Category

    static byte[] Get(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k)
    {
        uint type; int req;
        var buf = new byte[1024];
        return SetupDiGetDevicePropertyW(set, ref d, ref k, out type, buf, buf.Length, out req, 0) ? Trim(buf, req) : null;
    }
    static byte[] Trim(byte[] b, int n) { var r = new byte[n]; Array.Copy(b, r, n); return r; }
    static string Str(byte[] b) { return b == null ? null : Encoding.Unicode.GetString(b).Split('\0')[0]; }

    public static List<BtDevice> List()
    {
        var list = new List<BtDevice>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "BTHLE", IntPtr.Zero, 0x6); // ALLCLASSES | PRESENT
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                byte[] bat = Get(set, ref d, KBattery);
                if (bat == null || bat.Length < 1 || bat[0] > 100) continue;
                var dev = new BtDevice();
                dev.Percent = bat[0];
                byte[] ch = Get(set, ref d, KCharging);
                dev.Charging = ch != null && ch.Length > 0 && ch[0] != 0;
                byte[] up = Get(set, ref d, KUpdated);
                if (up != null && up.Length >= 8) dev.Updated = DateTime.FromFileTime(BitConverter.ToInt64(up, 0));
                dev.Name = Str(Get(set, ref d, KName)) ?? "Bluetooth";
                dev.Id = Str(Get(set, ref d, KInstance)) ?? dev.Name;
                dev.Category = Str(Get(set, ref d, KCategory)) ?? "";
                list.Add(dev);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
}

// ======================= App da bandeja =======================

class App : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string CfgKey = @"Software\MouseBatteryRedragon";
    const string AppName = "MouseBatteryRedragon";
    static readonly int[] Intervals = { 5, 10, 30, 60, 300 };

    NotifyIcon mouseTray = new NotifyIcon();
    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    ToolStripMenuItem startupItem;
    List<ToolStripMenuItem> intervalItems = new List<ToolStripMenuItem>();
    Control ui = new Control();
    volatile bool busy;

    MouseReading mouse;
    int mouseRealPct = -1; DateTime mouseRealTime; // último nível real (sem cabo)
    bool warned20, warned10;
    DateTime dischargeStart; int dischargeStartPct = -1;

    // Bluetooth: um ícone por dispositivo escolhido.
    ContextMenuStrip menu = new ContextMenuStrip();
    ToolStripMenuItem btMenu = new ToolStripMenuItem("Dispositivos Bluetooth");
    Dictionary<string, NotifyIcon> btTrays = new Dictionary<string, NotifyIcon>();
    List<BtDevice> bt = new List<BtDevice>();
    HashSet<string> btWarned = new HashSet<string>();
    HashSet<string> btShown, btHidden; // escolhas do usuário; sem escolha = mostra se atualizou nos últimos 3 dias

    App()
    {
        ui.CreateControl();
        LoadSaved();

        menu.Items.Add("Detalhes…", null, delegate { ShowDetails(); });
        menu.Items.Add("Atualizar agora", null, delegate { RefreshMouse(); RefreshBt(); });
        btMenu.DropDownItems.Add("(nenhum)");
        btMenu.DropDownOpening += delegate { BuildBtMenu(); };
        menu.Items.Add(btMenu);
        var freq = new ToolStripMenuItem("Atualizar a cada");
        foreach (int s in Intervals)
        {
            int sec = s;
            var it = new ToolStripMenuItem(sec < 60 ? sec + " segundos" : (sec / 60) + (sec == 60 ? " minuto" : " minutos"), null,
                delegate { SetInterval(sec); });
            it.Tag = sec;
            intervalItems.Add(it);
            freq.DropDownItems.Add(it);
        }
        menu.Items.Add(freq);
        startupItem = new ToolStripMenuItem("Iniciar com o Windows", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupEnabled();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, delegate
        {
            mouseTray.Visible = false;
            foreach (var t in btTrays.Values) t.Visible = false;
            Application.Exit();
        });

        Attach(mouseTray);
        SetIcon(mouseTray, Icons.Mouse("…", Color.Gray, false), "Mouse: lendo…");
        mouseTray.Visible = true;

        timer.Tick += delegate { RefreshMouse(); RefreshBt(); };
        SetInterval(LoadInterval());
        RefreshMouse();
        RefreshBt();
    }

    void Attach(NotifyIcon t)
    {
        t.ContextMenuStrip = menu;
        t.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };
    }

    // ---------- Bluetooth ----------

    void RefreshBt()
    {
        try { bt = Bluetooth.List(); } catch { return; }
        var seen = new HashSet<string>();
        foreach (var d in bt)
        {
            seen.Add(d.Id);
            NotifyIcon t;
            if (!btTrays.TryGetValue(d.Id, out t)) { t = new NotifyIcon(); Attach(t); btTrays[d.Id] = t; }
            bool stale = (DateTime.Now - d.Updated).TotalHours > 24;
            Color c = stale ? Color.Gray : d.Charging ? Color.DeepSkyBlue : d.Percent <= 15 ? Color.Red : d.Percent <= 30 ? Color.Orange : Color.LimeGreen;
            SetIcon(t, Icons.Bluetooth(d.Charging ? "" : d.Percent >= 100 ? "F" : d.Percent.ToString(), c, d.Charging),
                d.Name + ": " + (d.Charging ? "carregando" : d.Percent + "%") + "\nAtualizado " + When(d.Updated));
            t.Visible = IsShown(d);

            if (d.Charging || d.Percent > 25 || stale) btWarned.Remove(d.Id);
            else if (d.Percent <= 20 && t.Visible && btWarned.Add(d.Id))
                t.ShowBalloonTip(8000, "Bateria baixa: " + d.Name, "Restam " + d.Percent + "%.", ToolTipIcon.Info);
        }
        foreach (var id in new List<string>(btTrays.Keys))
            if (!seen.Contains(id)) { btTrays[id].Visible = false; btTrays[id].Dispose(); btTrays.Remove(id); }
    }

    bool IsShown(BtDevice d)
    {
        if (btShown.Contains(d.Id)) return true;
        if (btHidden.Contains(d.Id)) return false;
        return d.Updated != DateTime.MinValue && (DateTime.Now - d.Updated).TotalDays <= 3;
    }

    void BuildBtMenu()
    {
        btMenu.DropDownItems.Clear();
        if (bt.Count == 0) { btMenu.DropDownItems.Add("(nenhum com bateria)").Enabled = false; return; }
        foreach (var d in bt)
        {
            var dev = d;
            var it = new ToolStripMenuItem(d.Name + " — " + (d.Charging ? "carregando" : d.Percent + "%") + " (" + When(d.Updated) + ")");
            it.Checked = IsShown(d);
            it.Click += delegate
            {
                bool show = !IsShown(dev);
                btShown.Remove(dev.Id); btHidden.Remove(dev.Id);
                (show ? btShown : btHidden).Add(dev.Id);
                Save("BtShown", new List<string>(btShown).ToArray());
                Save("BtHidden", new List<string>(btHidden).ToArray());
                RefreshBt();
            };
            btMenu.DropDownItems.Add(it);
        }
    }

    static string When(DateTime t)
    {
        if (t == DateTime.MinValue) return "nunca";
        if (t.Date == DateTime.Today) return "hoje " + t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return "ontem " + t.ToString("HH:mm");
        return t.ToString("dd/MM HH:mm");
    }

    // ---------- mouse ----------

    void RefreshMouse()
    {
        if (busy) return;
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            MouseReading r = Mouse.Read();
            try { ui.BeginInvoke((Action)delegate { OnMouse(r); busy = false; }); }
            catch { busy = false; }
        });
    }

    void OnMouse(MouseReading r)
    {
        if (!r.Ok)
        {
            string old = mouseRealPct >= 0 ? " (último: " + mouseRealPct + "%)" : "";
            SetIcon(mouseTray, Icons.Mouse("?", Color.Gray, false), "Mouse desligado/dormindo" + old);
            return;
        }
        mouse = r;
        string when = DateTime.Now.ToString("HH:mm:ss");

        if (r.Charging)
        {
            dischargeStartPct = -1; warned20 = warned10 = false;
            if (r.Percent >= 100)
                SetIcon(mouseTray, Icons.Mouse("F", Color.DeepSkyBlue, false), "Mouse: carregado\n" + when);
            else
                SetIcon(mouseTray, Icons.Mouse(mouseRealPct >= 0 ? mouseRealPct.ToString() : "", Color.DeepSkyBlue, true),
                    "Mouse carregando" + (mouseRealPct >= 0 ? " (antes: " + mouseRealPct + "%)" : "") + "\n" + when);
            return;
        }

        mouseRealPct = r.Percent; mouseRealTime = r.Time;
        Save("MouseLastPct", r.Percent);
        TrackDischarge(r);

        Color c = r.Percent <= 15 ? Color.Red : r.Percent <= 30 ? Color.Orange : Color.LimeGreen;
        SetIcon(mouseTray, Icons.Mouse(r.Percent >= 100 ? "F" : r.Percent.ToString(), c, false),
            "Mouse: " + r.Percent + "%\n" + (r.Wired ? "Cabo" : "Dongle 2.4G") + " · " + when);

        if (r.Percent > 25) { warned20 = warned10 = false; return; }
        if (r.Percent <= 10 && !warned10)
        {
            warned10 = warned20 = true;
            mouseTray.ShowBalloonTip(10000, "Bateria do mouse crítica", "Restam " + r.Percent + "%. Coloque o mouse para carregar.", ToolTipIcon.Warning);
        }
        else if (r.Percent <= 20 && !warned20)
        {
            warned20 = true;
            mouseTray.ShowBalloonTip(8000, "Bateria do mouse baixa", "Restam " + r.Percent + "%.", ToolTipIcon.Info);
        }
    }

    void TrackDischarge(MouseReading r)
    {
        if (dischargeStartPct < 0 || r.Percent > dischargeStartPct) { dischargeStart = r.Time; dischargeStartPct = r.Percent; }
    }

    // Estimativa só depois de cair pelo menos 5 pontos, para não chutar.
    string Remaining()
    {
        if (mouse == null || mouse.Charging || dischargeStartPct < 0) return null;
        int drop = dischargeStartPct - mouse.Percent;
        if (drop < 5) return "calculando (precisa cair uns 5%)";
        double h = (mouse.Time - dischargeStart).TotalHours / drop * mouse.Percent;
        return h >= 24 ? "~" + (h / 24).ToString("0.0") + " dias" : "~" + h.ToString("0") + " horas";
    }

    // ---------- detalhes ----------

    void ShowDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MOUSE");
        if (mouse == null) sb.AppendLine("Sem leitura. O mouse está ligado?");
        else
        {
            if (mouse.Charging)
                sb.AppendLine("Bateria:\t" + (mouse.Percent >= 100 ? "carregado" : "carregando") +
                    (mouseRealPct >= 0 ? " (antes do cabo: " + mouseRealPct + "%)" : ""));
            else sb.AppendLine("Bateria:\t" + mouse.Percent + "%");
            sb.AppendLine("Voltagem:\t" + (mouse.MilliVolts / 1000.0).ToString("0.000") + " V" + (mouse.Charging ? " (com carga)" : ""));
            string rem = Remaining();
            if (rem != null) sb.AppendLine("Restante:\t" + rem);
            sb.AppendLine("Conexão:\t" + (mouse.Wired ? "Cabo USB" : "Dongle 2.4G"));
            if (mouse.PollingHz > 0) sb.AppendLine("Polling:\t" + mouse.PollingHz + " Hz");
            if (mouse.DpiLevels > 0) sb.AppendLine("DPI:\tnível " + mouse.DpiLevel + " de " + mouse.DpiLevels);
            sb.AppendLine("Leitura:\t" + mouse.Time.ToString("HH:mm:ss") + " (a cada " + timer.Interval / 1000 + " s)");
        }
        foreach (var d in bt)
        {
            if (!IsShown(d)) continue;
            sb.AppendLine();
            sb.AppendLine(d.Name.ToUpperInvariant() + " (Bluetooth)");
            sb.AppendLine("Bateria:\t" + (d.Charging ? "carregando" : d.Percent + "%"));
            sb.AppendLine("Atualizado:\t" + When(d.Updated));
        }
        MessageBox.Show(sb.ToString(), "Bateria dos dispositivos", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void SetIcon(NotifyIcon tray, Icon icon, string tip)
    {
        Icon old = tray.Icon;
        tray.Icon = icon;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // limite do Windows Forms
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // ---------- configurações ----------

    void SetInterval(int sec)
    {
        timer.Interval = sec * 1000;
        timer.Stop(); timer.Start();
        foreach (var it in intervalItems) it.Checked = (int)it.Tag == sec;
        Save("IntervalSeconds", sec);
    }

    static void Save(string name, object value)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(CfgKey)) k.SetValue(name, value); } catch { }
    }

    static object Load(string name)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(CfgKey)) return k == null ? null : k.GetValue(name);
    }

    void LoadSaved()
    {
        object v = Load("MouseLastPct"); if (v is int) mouseRealPct = (int)v;
        btShown = new HashSet<string>(Load("BtShown") as string[] ?? new string[0]);
        btHidden = new HashSet<string>(Load("BtHidden") as string[] ?? new string[0]);
    }

    static int LoadInterval()
    {
        object v = Load("IntervalSeconds");
        int s = v is int ? (int)v : 10;
        return Array.IndexOf(Intervals, s) >= 0 ? s : 10;
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsStartupEnabled()) k.DeleteValue(AppName, false);
            else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }
        startupItem.Checked = IsStartupEnabled();
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            MouseReading r = Mouse.Read();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "teste.txt"), r.Ok
                ? r.Percent + "% carregando=" + r.Charging + " " + r.MilliVolts + "mV cabo=" + r.Wired +
                  " polling=" + r.PollingHz + " dpi=" + r.DpiLevel + "/" + r.DpiLevels
                : "sem leitura");
            foreach (var d in Bluetooth.List())
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "teste.txt"),
                    "\r\nBT " + d.Name + " " + d.Percent + "% carregando=" + d.Charging + " atualizado=" + d.Updated.ToString("dd/MM HH:mm") + " cat=" + d.Category);
            return;
        }
        if (args.Length > 1 && args[0] == "--icons") { Icons.Preview(args[1]); return; }
        bool isNew;
        using (new Mutex(true, AppName, out isNew))
        {
            if (!isNew) return; // já está rodando
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
    }
}

// ======================= Ícones =======================

static class Icons
{
    // Número da bateria; carregando = raio. Mouse: barra embaixo. Bluetooth: barra em cima.
    public static Icon Mouse(string text, Color color, bool charging) { return Make(Draw(text, color, charging, false)); }
    public static Icon Bluetooth(string text, Color color, bool charging) { return Make(Draw(text, color, charging, true)); }

    static Icon Make(Bitmap bmp) { using (bmp) return Icon.FromHandle(bmp.GetHicon()); }

    static Bitmap Draw(string text, Color color, bool charging, bool barTop)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            float top = barTop ? 4 : 0, bottom = barTop ? 32 : 28;
            g.FillRectangle(brush, 2, barTop ? 1 : 28, 28, 3);

            if (charging)
            {
                // Raio de carregando; se tiver o último valor, ele aparece pequeno ao lado.
                float x = text.Length > 0 ? 0 : 8;
                g.FillPolygon(brush, new[] {
                    new PointF(x + 10, top), new PointF(x + 2, top + (bottom - top) * 0.58f), new PointF(x + 8, top + (bottom - top) * 0.58f),
                    new PointF(x + 5, bottom), new PointF(x + 15, top + (bottom - top) * 0.38f), new PointF(x + 9, top + (bottom - top) * 0.38f),
                    new PointF(x + 13, top) });
                if (text.Length > 0)
                    using (var f = new Font("Segoe UI", text.Length >= 3 ? 9 : 13, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, f, brush, new RectangleF(13, top, 20, bottom - top), sf);
            }
            else
            {
                float size = text.Length >= 3 ? 13 : text.Length == 2 ? 19 : 22;
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text.Length > 0 ? text : "–", f, brush, new RectangleF(-2, top, 36, bottom - top), sf);
            }
        }
        return bmp;
    }

    // Gera um PNG com todos os estados dos ícones, para conferir o desenho.
    public static void Preview(string file)
    {
        var states = new List<Bitmap> {
            Draw("70", Color.LimeGreen, false, false), Draw("25", Color.Orange, false, false), Draw("8", Color.Red, false, false),
            Draw("70", Color.DeepSkyBlue, true, false), Draw("", Color.DeepSkyBlue, true, false), Draw("F", Color.DeepSkyBlue, false, false),
            Draw("?", Color.Gray, false, false),
            Draw("F", Color.LimeGreen, false, true), Draw("18", Color.Orange, false, true), Draw("", Color.DeepSkyBlue, true, true),
            Draw("6", Color.Gray, false, true) };
        using (var sheet = new Bitmap(states.Count * 40 + 8, 48))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            for (int i = 0; i < states.Count; i++) { g.DrawImage(states[i], 8 + i * 40, 8, 32, 32); states[i].Dispose(); }
            sheet.Save(file);
        }
    }
}
