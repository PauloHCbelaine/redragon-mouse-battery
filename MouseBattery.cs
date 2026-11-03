// Monitor de bateria para mouses Redragon com chip Compx (VID 0x3554), testado no M916 PRO 1K.
// Fica na bandeja do Windows mostrando a porcentagem e avisa quando a bateria fica baixa.
// Só usa comandos de LEITURA (0x04 bateria, 0x08 leitura de memória); nunca altera o mouse.
using System;
using System.Collections.Generic;
using System.Drawing;
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

    // Caminhos da interface de comandos (interface 1, coleção 5) de todos os dispositivos Compx.
    public static List<string> FindPaths()
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
                        if (p.Contains("vid_3554") && p.Contains("mi_01") && p.Contains("col05")) result.Add(p);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        // Dongle (2.4G) primeiro, cabo depois.
        result.Sort((a, b) => (b.Contains("pid_f5d5") ? 1 : 0) - (a.Contains("pid_f5d5") ? 1 : 0));
        return result;
    }

    public static SafeFileHandle Open(string path)
    {
        return CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // RW, share RW, OPEN_EXISTING, OVERLAPPED
    }
}

// Conversa com o mouse. O driver oficial da Redragon pode estar usando o mesmo canal ao mesmo
// tempo, então respostas podem se perder: cada pedido é repetido algumas vezes.
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
                if (left < TimeSpan.Zero || !ar.AsyncWaitHandle.WaitOne(left)) return null; // handle fechado depois cancela a leitura
                int n = fs.EndRead(ar);
                if (n >= 17 && buf[0] == 0x08 && buf[1] == cmd && buf[2] == 0 &&
                    (cmd != 0x08 || (buf[3] == req[3] && buf[4] == req[4])))
                    return buf;
            }
        }
        return null;
    }
}

class Reading
{
    public bool Ok; public int Percent; public bool Charging; public int MilliVolts; public bool Wired;
    public int PollingHz;       // 0 = desconhecido
    public int DpiLevel, DpiLevels; // nível ativo (1..N) e quantidade de níveis; 0 = desconhecido
    public DateTime Time = DateTime.Now;
}

class App : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string CfgKey = @"Software\MouseBatteryRedragon";
    const string AppName = "MouseBatteryRedragon";
    static readonly int[] Intervals = { 5, 10, 30, 60, 300 };

    NotifyIcon tray = new NotifyIcon();
    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    ToolStripMenuItem startupItem;
    List<ToolStripMenuItem> intervalItems = new List<ToolStripMenuItem>();
    Reading last;
    bool warned20, warned10;
    volatile bool busy;
    // Para estimar o tempo restante: primeira leitura desde que tirou do carregador.
    DateTime dischargeStart; int dischargeStartPct = -1;

    App()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Detalhes…", null, delegate { ShowDetails(); });
        menu.Items.Add("Atualizar agora", null, delegate { Refresh(); });
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
        menu.Items.Add("Sair", null, delegate { tray.Visible = false; Application.Exit(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };
        tray.Icon = Draw("…", Color.Gray);
        tray.Text = "Bateria do mouse: lendo…";
        tray.Visible = true;

        timer.Tick += delegate { Refresh(); };
        SetInterval(LoadInterval());
        Refresh();
    }

    // ---------- leitura ----------

    void Refresh()
    {
        if (busy) return;
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Reading r = Read();
            try { tray.ContextMenuStrip.BeginInvoke((Action)delegate { Show(r); busy = false; }); }
            catch { busy = false; }
        });
    }

    public static Reading Read()
    {
        foreach (string p in Hid.FindPaths())
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
                        var r = new Reading();
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
        return new Reading();
    }

    static int PollingHz(byte v)
    {
        switch (v)
        {
            case 0x08: return 125; case 0x04: return 250; case 0x02: return 500; case 0x01: return 1000;
            case 0x10: return 2000; case 0x20: return 4000; case 0x40: return 8000; default: return 0;
        }
    }

    // ---------- exibição ----------

    void Show(Reading r)
    {
        if (!r.Ok)
        {
            string old = last != null ? " (último: " + last.Percent + "%)" : "";
            SetIcon(Draw("?", Color.Gray), "Mouse desligado/dormindo" + old);
            return;
        }
        last = r;
        TrackDischarge(r);

        Color c = r.Charging ? Color.DeepSkyBlue : r.Percent <= 15 ? Color.Red : r.Percent <= 30 ? Color.Orange : Color.LimeGreen;
        string tip = "Mouse: " + r.Percent + "%" + (r.Charging ? " carregando" : "") +
                     "\n" + (r.Wired ? "Cabo" : "Dongle 2.4G") + " · " + DateTime.Now.ToString("HH:mm:ss");
        SetIcon(Draw(r.Percent >= 100 ? "F" : r.Percent.ToString(), c), tip);

        if (r.Charging || r.Percent > 25) { warned20 = warned10 = false; return; }
        if (r.Percent <= 10 && !warned10)
        {
            warned10 = warned20 = true;
            tray.ShowBalloonTip(10000, "Bateria do mouse crítica", "Restam " + r.Percent + "%. Coloque o mouse para carregar.", ToolTipIcon.Warning);
        }
        else if (r.Percent <= 20 && !warned20)
        {
            warned20 = true;
            tray.ShowBalloonTip(8000, "Bateria do mouse baixa", "Restam " + r.Percent + "%.", ToolTipIcon.Info);
        }
    }

    void TrackDischarge(Reading r)
    {
        if (r.Charging) { dischargeStartPct = -1; return; }
        if (dischargeStartPct < 0 || r.Percent > dischargeStartPct) { dischargeStart = r.Time; dischargeStartPct = r.Percent; }
    }

    // Estimativa só depois de cair pelo menos 5 pontos, para não chutar.
    string Remaining()
    {
        if (last == null || last.Charging || dischargeStartPct < 0) return null;
        int drop = dischargeStartPct - last.Percent;
        if (drop < 5) return "calculando (precisa cair uns 5%)";
        double hoursPerPct = (last.Time - dischargeStart).TotalHours / drop;
        double h = hoursPerPct * last.Percent;
        return h >= 24 ? "~" + (h / 24).ToString("0.0") + " dias" : "~" + h.ToString("0") + " horas";
    }

    void ShowDetails()
    {
        var sb = new StringBuilder();
        if (last == null) sb.AppendLine("Ainda sem leitura. O mouse está ligado?");
        else
        {
            sb.AppendLine("Bateria:\t" + last.Percent + "%" + (last.Charging ? " (carregando)" : ""));
            sb.AppendLine("Voltagem:\t" + (last.MilliVolts / 1000.0).ToString("0.000") + " V");
            string rem = Remaining();
            if (rem != null) sb.AppendLine("Restante:\t" + rem);
            sb.AppendLine("Conexão:\t" + (last.Wired ? "Cabo USB" : "Dongle 2.4G"));
            if (last.PollingHz > 0) sb.AppendLine("Polling:\t" + last.PollingHz + " Hz");
            if (last.DpiLevels > 0) sb.AppendLine("DPI:\tnível " + last.DpiLevel + " de " + last.DpiLevels);
            sb.AppendLine("Leitura:\t" + last.Time.ToString("HH:mm:ss"));
        }
        sb.AppendLine();
        sb.Append("Atualiza a cada " + timer.Interval / 1000 + " s");
        MessageBox.Show(sb.ToString(), "Mouse Redragon", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SetIcon(Icon icon, string tip)
    {
        Icon old = tray.Icon;
        tray.Icon = icon;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // limite do Windows Forms
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // Desenha o número da bateria direto no ícone da bandeja.
    static Icon Draw(string text, Color color)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            float size = text.Length >= 3 ? 13 : text.Length == 2 ? 19 : 22;
            using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, brush, new RectangleF(-2, 0, 36, 32), sf);
            using (var bar = new SolidBrush(color)) g.FillRectangle(bar, 2, 28, 28, 3);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ---------- configurações ----------

    void SetInterval(int sec)
    {
        timer.Interval = sec * 1000;
        timer.Stop(); timer.Start();
        foreach (var it in intervalItems) it.Checked = (int)it.Tag == sec;
        using (var k = Registry.CurrentUser.CreateSubKey(CfgKey)) k.SetValue("IntervalSeconds", sec);
    }

    static int LoadInterval()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(CfgKey))
        {
            object v = k == null ? null : k.GetValue("IntervalSeconds");
            int s = v is int ? (int)v : 10;
            return Array.IndexOf(Intervals, s) >= 0 ? s : 10;
        }
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
            Reading r = Read();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "teste.txt"), r.Ok
                ? r.Percent + "% carregando=" + r.Charging + " " + r.MilliVolts + "mV cabo=" + r.Wired +
                  " polling=" + r.PollingHz + " dpi=" + r.DpiLevel + "/" + r.DpiLevels
                : "sem leitura");
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
