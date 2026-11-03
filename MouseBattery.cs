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

    App()
    {
        ui.CreateControl();
        LoadSaved();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Detalhes…", null, delegate { ShowDetails(); });
        menu.Items.Add("Atualizar agora", null, delegate { RefreshMouse(); });
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
        menu.Items.Add("Sair", null, delegate { mouseTray.Visible = false; Application.Exit(); });

        mouseTray.ContextMenuStrip = menu;
        mouseTray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };
        SetIcon(mouseTray, Icons.Mouse("…", Color.Gray, false), "Mouse: lendo…");
        mouseTray.Visible = true;

        timer.Tick += delegate { RefreshMouse(); };
        SetInterval(LoadInterval());
        RefreshMouse();
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
        MessageBox.Show(sb.ToString(), "Mouse Redragon", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    // Número da bateria com uma barra embaixo; carregando = raio.
    public static Icon Mouse(string text, Color color, bool charging)
    {
        using (var bmp = Draw(text, color, charging)) return Icon.FromHandle(bmp.GetHicon());
    }

    static Bitmap Draw(string text, Color color, bool charging)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            const float top = 0, bottom = 28;
            g.FillRectangle(brush, 2, 28, 28, 3);

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
            Draw("70", Color.LimeGreen, false), Draw("25", Color.Orange, false), Draw("8", Color.Red, false),
            Draw("70", Color.DeepSkyBlue, true), Draw("", Color.DeepSkyBlue, true), Draw("F", Color.DeepSkyBlue, false),
            Draw("?", Color.Gray, false) };
        using (var sheet = new Bitmap(states.Count * 40 + 8, 48))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            for (int i = 0; i < states.Count; i++) { g.DrawImage(states[i], 8 + i * 40, 8, 32, 32); states[i].Dispose(); }
            sheet.Save(file);
        }
    }
}
