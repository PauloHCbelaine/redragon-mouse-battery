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

    // Um pedido por vez para o mouse, mesmo vindo de lugares diferentes do app.
    static readonly object Busy = new object();

    // cmd 0x04 = bateria; 0x08 = ler `len` bytes da memória em `addr`; 0x07 = gravar `data` em `addr`.
    public byte[] Query(byte cmd, int addr, int len, byte[] data = null)
    {
        var req = new byte[17];
        req[0] = 0x08; req[1] = cmd; req[3] = (byte)(addr >> 8); req[4] = (byte)addr;
        req[5] = (byte)(data != null ? data.Length : len);
        if (data != null) Array.Copy(data, 0, req, 6, data.Length);
        int sum = 0; for (int i = 0; i < 16; i++) sum += req[i];
        req[16] = (byte)((0x55 - sum) & 0xFF);

        lock (Busy)
        {
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
                        (cmd != 0x08 && cmd != 0x07 || (buf[3] == req[3] && buf[4] == req[4])))
                        return buf;
                }
            }
        }
        return null;
    }

    public byte[] ReadMem(int addr, int len)
    {
        byte[] r = Query(0x08, addr, len);
        if (r == null) return null;
        var d = new byte[len]; Array.Copy(r, 6, d, 0, len); return d;
    }

    // Grava e confere lendo de volta.
    public bool WriteMem(int addr, byte[] data)
    {
        if (Query(0x07, addr, 0, data) == null) return false;
        byte[] back = ReadMem(addr, data.Length);
        if (back == null) return false;
        for (int i = 0; i < data.Length; i++) if (back[i] != data[i]) return false;
        return true;
    }

    // Valor simples gravado em par com o byte de verificação (0x55 - valor).
    public static byte[] Pair(int v) { return new[] { (byte)v, (byte)((0x55 - v) & 0xFF) }; }

    // Abre o mouse (dongle primeiro) e executa `work`. Devolve null se o mouse não responder.
    public static T With<T>(Func<Mouse, T> work) where T : class
    {
        var paths = Hid.FindPaths("vid_3554", "mi_01", "col05");
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
                        if (m.Query(0x04, 0, 0) == null) continue; // não está respondendo por este caminho
                        T result = work(m);
                        if (result != null) return result;
                    }
                }
            }
            catch { }
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

// Configurações do mouse. Endereços confirmados no M916 PRO 1K comparando a memória
// antes/depois de mudar cada opção no driver oficial da Redragon.
class MouseSettings
{
    public const int MaxLevels = 8;
    public int Polling = 1000;           // 0x0000
    public int Levels = 5, Active = 1;   // 0x0002 (quantidade), 0x0004 (ativo, 1..N)
    public int LodMm = 1;                // 0x000A (1 ou 2 mm)
    public int PeakSeconds = 30;         // 0x00B7 (em unidades de 10 s)
    public int[] Dpi = new int[MaxLevels]; // 0x000C + 4*i
    byte[] dpiRaw = new byte[MaxLevels * 4];

    public static readonly int[] PollingOptions = { 125, 250, 500, 1000 };
    static int PollCode(int hz) { return hz == 125 ? 0x08 : hz == 250 ? 0x04 : hz == 500 ? 0x02 : 0x01; }
    static int PollHz(int code) { return code == 0x08 ? 125 : code == 0x04 ? 250 : code == 0x02 ? 500 : 1000; }

    // DPI = (código + 1) × 50. Os 2 bits altos do código vão no 3º byte (bits 2-3 do X e do Y).
    static int DecodeDpi(byte lo, byte nib) { return ((((nib >> 2) & 3) << 8 | lo) + 1) * 50; }
    static byte[] EncodeDpi(int dpi, byte oldNib)
    {
        int v = Math.Max(0, Math.Min(519, dpi / 50 - 1));
        byte lo = (byte)(v & 0xFF);
        int hi = (v >> 8) & 3;
        byte nib = (byte)((oldNib & 0x33) | (hi << 2) | (hi << 6));
        return new[] { lo, lo, nib, (byte)((0x55 - lo - lo - nib) & 0xFF) };
    }

    public static MouseSettings Read(Mouse m)
    {
        var s = new MouseSettings();
        byte[] a = m.ReadMem(0x0000, 6), lod = m.ReadMem(0x000A, 2), peak = m.ReadMem(0x00B7, 2);
        if (a == null || lod == null || peak == null) return null;
        s.Polling = PollHz(a[0]); s.Levels = a[2]; s.Active = a[4] + 1;
        s.LodMm = lod[0]; s.PeakSeconds = peak[0] * 10;
        for (int i = 0; i < MaxLevels; i += 2)
        {
            byte[] w = m.ReadMem(0x000C + 4 * i, 8);
            if (w == null) return null;
            Array.Copy(w, 0, s.dpiRaw, 4 * i, 8);
        }
        for (int i = 0; i < MaxLevels; i++) s.Dpi[i] = DecodeDpi(s.dpiRaw[4 * i], s.dpiRaw[4 * i + 2]);
        if (s.Levels < 1 || s.Levels > MaxLevels || s.Active > s.Levels) return null;
        return s;
    }

    // Grava só o que mudou em relação a `old`. Devolve o que falhou ("" = tudo certo).
    public string Apply(Mouse m, MouseSettings old)
    {
        var errors = new List<string>();
        for (int i = 0; i < MaxLevels; i++)
            if (i < Levels && Dpi[i] != old.Dpi[i] && !m.WriteMem(0x000C + 4 * i, EncodeDpi(Dpi[i], old.dpiRaw[4 * i + 2])))
                errors.Add("DPI " + (i + 1));
        if (Levels != old.Levels && !m.WriteMem(0x0002, Mouse.Pair(Levels))) errors.Add("quantidade de níveis");
        if (Active != old.Active && !m.WriteMem(0x0004, Mouse.Pair(Active - 1))) errors.Add("nível ativo");
        if (Polling != old.Polling && !m.WriteMem(0x0000, Mouse.Pair(PollCode(Polling)))) errors.Add("polling");
        if (LodMm != old.LodMm && !m.WriteMem(0x000A, Mouse.Pair(LodMm))) errors.Add("LOD");
        if (PeakSeconds != old.PeakSeconds && !m.WriteMem(0x00B7, Mouse.Pair(PeakSeconds / 10))) errors.Add("Peak Performance");
        return string.Join(", ", errors.ToArray());
    }

    public MouseSettings Copy() { var c = (MouseSettings)MemberwiseClone(); c.Dpi = (int[])Dpi.Clone(); c.dpiRaw = (byte[])dpiRaw.Clone(); return c; }

    // Perfil salvo como texto: polling|níveis|ativo|lod|peak|dpi1,dpi2,...
    public string Serialize()
    {
        var d = new string[Levels]; for (int i = 0; i < Levels; i++) d[i] = Dpi[i].ToString();
        return Polling + "|" + Levels + "|" + Active + "|" + LodMm + "|" + PeakSeconds + "|" + string.Join(",", d);
    }

    // Aplica um perfil por cima das configurações atuais (mantém os bytes que o perfil não guarda).
    public MouseSettings WithProfile(string text)
    {
        var p = text.Split('|');
        if (p.Length < 6) return null;
        var s = Copy();
        s.Polling = int.Parse(p[0]); s.Levels = int.Parse(p[1]); s.Active = int.Parse(p[2]);
        s.LodMm = int.Parse(p[3]); s.PeakSeconds = int.Parse(p[4]);
        var d = p[5].Split(',');
        for (int i = 0; i < d.Length && i < MaxLevels; i++) s.Dpi[i] = int.Parse(d[i]);
        if (s.Levels < 1 || s.Levels > MaxLevels || s.Active < 1 || s.Active > s.Levels) return null;
        return s;
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

// ======================= Teclado Anne Pro 2 (cabo USB) =======================

// Cor única para todas as teclas, com o comando que o Annemone descobriu no ObinsKit
// (github.com/illixion/Annemone). Só funciona com o teclado no cabo USB (interface 1).
static class Keyboard
{
    [StructLayout(LayoutKind.Sequential)]
    struct CAPS
    {
        public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] r;
        public ushort n1, n2, n3, n4, n5, n6, n7, n8, n9, n10;
    }
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);

    static readonly string[] Pids = { "pid_8008", "pid_8009", "pid_a292", "pid_a293" };

    static string FindPath()
    {
        foreach (string p in Hid.FindPaths("vid_04d9", "mi_01"))
            foreach (string pid in Pids) if (p.Contains(pid)) return p;
        return null;
    }

    public static bool Connected { get { return FindPath() != null; } }

    public static bool SetColor(Color c)
    {
        string path = FindPath();
        if (path == null) return false;
        byte[] msg = { 0, 123, 16, 65, 16, 7, 0, 0, 125, 32, 3, 255, 1, c.R, c.G, c.B };
        using (var h = Hid.Open(path))
        {
            if (h.IsInvalid) return false;
            int len = 65;
            IntPtr pp;
            if (HidD_GetPreparsedData(h, out pp))
            {
                CAPS caps; if (HidP_GetCaps(pp, out caps) == 0x110000 && caps.OutLen > 0) len = caps.OutLen;
                HidD_FreePreparsedData(pp);
            }
            var buf = new byte[Math.Max(len, msg.Length)];
            Array.Copy(msg, buf, msg.Length);
            using (var fs = new FileStream(h, FileAccess.ReadWrite, buf.Length, true)) fs.Write(buf, 0, buf.Length);
        }
        return true;
    }
}

// Perfis guardados no registro do usuário.
static class Profiles
{
    const string Key = @"Software\MouseBatteryRedragon\Profiles";
    public static string[] Names()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(Key))
        {
            var n = k == null ? new string[0] : k.GetValueNames();
            Array.Sort(n, StringComparer.CurrentCultureIgnoreCase);
            return n;
        }
    }
    public static string Get(string name) { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue(name) as string; }
    public static void Save(string name, string data) { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, data); }
    public static void Delete(string name) { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.DeleteValue(name, false); }
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
    bool mouseOnline;
    int mouseRealPct = -1; DateTime mouseRealTime; // último nível real (sem cabo)
    bool warned20, warned10;
    DateTime dischargeStart; int dischargeStartPct = -1;

    // Bluetooth: um ícone por dispositivo escolhido.
    ContextMenuStrip menu = new ContextMenuStrip();
    ToolStripMenuItem btMenu = new ToolStripMenuItem("Dispositivos Bluetooth");
    ToolStripMenuItem profilesMenu = new ToolStripMenuItem("Perfis do mouse");
    ToolStripMenuItem keyboardMenu = new ToolStripMenuItem("Cor do teclado (Anne Pro 2)");
    SettingsWindow settingsForm;
    Dictionary<string, NotifyIcon> btTrays = new Dictionary<string, NotifyIcon>();
    List<BtDevice> bt = new List<BtDevice>();
    HashSet<string> btWarned = new HashSet<string>();
    HashSet<string> btShown, btHidden; // escolhas do usuário; sem escolha = mostra se atualizou nos últimos 3 dias

    App()
    {
        ui.CreateControl();
        LoadSaved();

        menu.Renderer = new ThemedRenderer();
        ToolStripManager.Renderer = menu.Renderer;
        menu.Font = Theme.Font(9.5f);
        var open = menu.Items.Add("Abrir painel", null, delegate { ShowDetails(); });
        open.Font = Theme.Semibold(9.5f);
        menu.Items.Add("Atualizar agora", null, delegate { RefreshMouse(); RefreshBt(); });
        btMenu.DropDownItems.Add("(nenhum)");
        btMenu.DropDownOpening += delegate { BuildBtMenu(); };
        menu.Items.Add(btMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configurar mouse…", null, delegate { OpenSettings(); });
        profilesMenu.DropDownItems.Add("(nenhum)");
        profilesMenu.DropDownOpening += delegate { BuildProfilesMenu(); };
        menu.Items.Add(profilesMenu);
        keyboardMenu.DropDownItems.Add("(nenhum)");
        keyboardMenu.DropDownOpening += delegate { BuildKeyboardMenu(); };
        menu.Items.Add(keyboardMenu);
        menu.Items.Add(new ToolStripSeparator());
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

    // ---------- edição do mouse ----------

    // Lê o estado atual do mouse e grava só as diferenças. null = mouse não respondeu.
    public static string ApplySettings(MouseSettings ignored, MouseSettings wanted)
    {
        return Mouse.With(m =>
        {
            var now = MouseSettings.Read(m);
            return now == null ? null : wanted.Apply(m, now);
        });
    }

    void OpenSettings()
    {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
        var s = Mouse.With(m => MouseSettings.Read(m));
        if (s == null)
        {
            MessageBox.Show("O mouse não respondeu. Ele está ligado e conectado (dongle ou cabo)?", "Configurar mouse",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        settingsForm = new SettingsWindow(s);
        settingsForm.FormClosed += delegate { RefreshMouse(); };
        settingsForm.Show();
    }

    void BuildProfilesMenu()
    {
        profilesMenu.DropDownItems.Clear();
        string[] names = Profiles.Names();
        if (names.Length == 0)
            profilesMenu.DropDownItems.Add("(crie perfis em \"Configurar mouse\")").Enabled = false;
        foreach (string n in names)
        {
            string name = n;
            profilesMenu.DropDownItems.Add(name, null, delegate { ApplyProfile(name); });
        }
    }

    void ApplyProfile(string name)
    {
        string err = Mouse.With(m =>
        {
            var now = MouseSettings.Read(m);
            if (now == null) return null;
            var wanted = now.WithProfile(Profiles.Get(name));
            return wanted == null ? "perfil inválido" : wanted.Apply(m, now);
        });
        if (err == null) mouseTray.ShowBalloonTip(5000, "Perfil não aplicado", "O mouse não respondeu.", ToolTipIcon.Warning);
        else if (err.Length > 0) mouseTray.ShowBalloonTip(5000, "Perfil aplicado em parte", "Falhou: " + err, ToolTipIcon.Warning);
        else mouseTray.ShowBalloonTip(3000, "Perfil \"" + name + "\" aplicado", "Configurações gravadas no mouse.", ToolTipIcon.Info);
        RefreshMouse();
    }

    // ---------- cor do teclado ----------

    static readonly string[] ColorNames = { "Branco", "Vermelho", "Verde", "Azul", "Roxo", "Ciano", "Amarelo", "Laranja", "Desligado" };
    static readonly Color[] ColorValues = { Color.White, Color.Red, Color.Lime, Color.Blue, Color.FromArgb(160, 0, 255),
        Color.Cyan, Color.Yellow, Color.FromArgb(255, 100, 0), Color.Black };

    void BuildKeyboardMenu()
    {
        keyboardMenu.DropDownItems.Clear();
        if (!Keyboard.Connected)
        {
            keyboardMenu.DropDownItems.Add("(ligue o teclado no cabo USB)").Enabled = false;
            return;
        }
        for (int i = 0; i < ColorNames.Length; i++)
        {
            Color c = ColorValues[i];
            var it = new ToolStripMenuItem(ColorNames[i], Swatch(c), delegate { SetKeyboardColor(c); });
            keyboardMenu.DropDownItems.Add(it);
        }
        keyboardMenu.DropDownItems.Add(new ToolStripSeparator());
        keyboardMenu.DropDownItems.Add("Outra cor…", null, delegate
        {
            using (var dlg = new ColorDialog { FullOpen = true })
                if (dlg.ShowDialog() == DialogResult.OK) SetKeyboardColor(dlg.Color);
        });
    }

    static Bitmap Swatch(Color c)
    {
        var b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b)) { g.Clear(c); g.DrawRectangle(Pens.Gray, 0, 0, 15, 15); }
        return b;
    }

    void SetKeyboardColor(Color c)
    {
        bool ok = false;
        try { ok = Keyboard.SetColor(c); } catch { }
        if (!ok) mouseTray.ShowBalloonTip(4000, "Teclado", "Não consegui falar com o teclado. Ele está no cabo USB?", ToolTipIcon.Warning);
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
        UpdateFlyout();
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
        mouseOnline = r.Ok;
        try { Update(r); } finally { UpdateFlyout(); }
    }

    void Update(MouseReading r)
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

    // Painel que abre ao clicar no ícone: um cartão por dispositivo.
    Flyout flyout;
    DateTime flyoutClosed;

    void ShowDetails()
    {
        if (flyout != null && !flyout.IsDisposed) { flyout.Close(); return; }
        if ((DateTime.Now - flyoutClosed).TotalMilliseconds < 300) return; // o clique que fechou o painel
        flyout = new Flyout();
        flyout.SettingsClicked += delegate { flyout.Close(); OpenSettings(); };
        flyout.RefreshClicked += delegate { RefreshMouse(); RefreshBt(); };
        flyout.MoreClicked += delegate
        {
            var at = Cursor.Position; flyout.Close();
            menu.Show(at, ToolStripDropDownDirection.AboveLeft);
        };
        flyout.FormClosed += delegate { flyoutClosed = DateTime.Now; };
        UpdateFlyout();
        flyout.Show();
    }

    void UpdateFlyout()
    {
        if (flyout == null || flyout.IsDisposed) return;
        flyout.SetCards(BuildCards(), "Atualiza a cada " + (timer.Interval / 1000 < 60 ? timer.Interval / 1000 + " s" : timer.Interval / 60000 + " min") +
            (mouse != null ? " · última leitura " + mouse.Time.ToString("HH:mm:ss") : ""));
    }

    List<DeviceCard> BuildCards()
    {
        var list = new List<DeviceCard>();
        var m = new DeviceCard { Glyph = "", Name = "Redragon M916 PRO" };
        if (mouse == null || !mouseOnline)
        {
            m.Percent = mouseRealPct; m.Stale = true;
            m.Status = "Desligado ou dormindo";
            m.Detail = mouseRealPct >= 0 ? "Último nível conhecido: " + mouseRealPct + "%" : "Mexa o mouse para ele acordar";
        }
        else
        {
            m.Charging = mouse.Charging;
            if (mouse.Charging)
            {
                m.Percent = mouse.Percent >= 100 ? 100 : mouseRealPct;
                m.Status = mouse.Percent >= 100 ? "Carregado" : "Carregando" + (mouseRealPct >= 0 ? " · antes do cabo " + mouseRealPct + "%" : "");
            }
            else
            {
                m.Percent = mouse.Percent;
                string rem = Remaining();
                m.Status = rem == null ? "Em uso" : rem.StartsWith("~") ? rem + " restantes" : "Tempo restante: " + rem;
            }
            var parts = new List<string> { mouse.Wired ? "Cabo USB" : "Dongle 2.4G" };
            if (mouse.PollingHz > 0) parts.Add(mouse.PollingHz + " Hz");
            if (mouse.DpiLevels > 0) parts.Add("DPI " + mouse.DpiLevel + "/" + mouse.DpiLevels);
            parts.Add((mouse.MilliVolts / 1000.0).ToString("0.00") + " V");
            m.Detail = string.Join("  ·  ", parts.ToArray());
        }
        list.Add(m);

        foreach (var d in bt)
        {
            if (!IsShown(d)) continue;
            string cat = (d.Category + " " + d.Name).ToLowerInvariant();
            var c = new DeviceCard
            {
                Name = d.Name, Percent = d.Percent, Charging = d.Charging,
                Stale = (DateTime.Now - d.Updated).TotalHours > 24,
                Glyph = cat.Contains("keyboard") || d.Name == "AP2" ? "" :
                        cat.Contains("gam") || cat.Contains("controller") || cat.Contains("8bitdo") ? "" :
                        cat.Contains("audio") || cat.Contains("head") || cat.Contains("buds") ? "" : "",
            };
            c.Status = d.Charging ? "Carregando" : c.Stale ? "Sem conexão recente" : "Bluetooth";
            c.Detail = "Atualizado " + When(d.Updated);
            list.Add(c);
        }
        return list;
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
        if (args.Length > 1 && args[0] == "--preview")
        {
            // Desenha o painel e a janela de configuração em PNG, para conferir o visual.
            var cards = new List<DeviceCard> {
                new DeviceCard { Glyph = "", Name = "Redragon M916 PRO", Percent = 82, Status = "~31 horas restantes", Detail = "Dongle 2.4G  ·  1000 Hz  ·  DPI 3/5  ·  4.02 V" },
                new DeviceCard { Glyph = "", Name = "AP2", Percent = 100, Status = "Bluetooth", Detail = "Atualizado hoje 13:53" },
                new DeviceCard { Glyph = "", Name = "8BitDo Ultimate 2C Wireless", Percent = 6, Stale = true, Status = "Sem conexão recente", Detail = "Atualizado 14/10 00:06" },
                new DeviceCard { Glyph = "", Name = "Mouse carregando (exemplo)", Percent = 65, Charging = true, Status = "Carregando · antes do cabo 65%", Detail = "Cabo USB  ·  1000 Hz" } };
            Action<Form, string> snap = (f, name) =>
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Show(); f.Location = new Point(-5000, 0); Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(args[1] + name); }
            };
            var fl = new Flyout(); fl.SetCards(cards, "Atualiza a cada 10 s · última leitura 17:58:02");
            snap(fl, "-painel.png");
            var st = Mouse.With(m => MouseSettings.Read(m)) ?? new MouseSettings();
            snap(new SettingsWindow(st), "-config.png");
            return;
        }
        if (args.Length > 0 && args[0] == "--settings")
        {
            var s = Mouse.With(m => MouseSettings.Read(m));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "teste.txt"),
                s == null ? "sem leitura" : s.Serialize() + " | todos: " + string.Join(",", Array.ConvertAll(s.Dpi, x => x.ToString())));
            return;
        }
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
