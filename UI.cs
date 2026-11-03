// Interface visual: tema (segue claro/escuro e a cor de destaque do Windows), componentes
// desenhados à mão, o painel de dispositivos e a janela "Configurar mouse".
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================= Tema =======================

static class Theme
{
    public static bool Dark;
    public static Color Bg, Card, Control, ControlHover, Border, Text, Sub, Track, Accent, AccentText;
    public static Color Good, Warn, Bad, Charge, Stale;
    public static string IconFont;

    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

    static Theme() { Load(); }

    public static void Load()
    {
        Dark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        if (Dark)
        {
            Bg = Hex(0x202020); Card = Hex(0x2B2B2B); Control = Hex(0x373737); ControlHover = Hex(0x404040);
            Border = Hex(0x3C3C3C); Text = Hex(0xFFFFFF); Sub = Hex(0xA8A8A8); Track = Hex(0x454545);
            Good = Hex(0x4CC76A); Warn = Hex(0xF2A93B); Bad = Hex(0xF0605D); Charge = Hex(0x4CC2FF); Stale = Hex(0x7A7A7A);
        }
        else
        {
            Bg = Hex(0xF3F3F3); Card = Hex(0xFFFFFF); Control = Hex(0xF0F0F0); ControlHover = Hex(0xE6E6E6);
            Border = Hex(0xE2E2E2); Text = Hex(0x1B1B1B); Sub = Hex(0x5F5F5F); Track = Hex(0xE4E4E4);
            Good = Hex(0x13A10E); Warn = Hex(0xCA7A00); Bad = Hex(0xD13438); Charge = Hex(0x0067C0); Stale = Hex(0x9A9A9A);
        }
        // Cor de destaque do Windows (guardada como ABGR).
        int a = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", -1);
        Accent = a == -1 ? Color.Empty : Color.FromArgb(a & 0xFF, (a >> 8) & 0xFF, (a >> 16) & 0xFF);
        // Destaque cinza (ou ausente) não dá contraste: usa o azul padrão do Windows.
        if (Accent.IsEmpty || Math.Max(Accent.R, Math.Max(Accent.G, Accent.B)) - Math.Min(Accent.R, Math.Min(Accent.G, Accent.B)) < 40)
            Accent = Dark ? Hex(0x4CC2FF) : Hex(0x0067C0);
        if (Dark) Accent = Mix(Accent, Color.White, Luma(Accent) < 0.35 ? 0.35 : 0.1); // mais legível no escuro
        AccentText = Luma(Accent) > 0.55 ? Color.Black : Color.White;

        IconFont = "Segoe MDL2 Assets";
        using (var fc = new InstalledFontCollection())
            foreach (var f in fc.Families) if (f.Name == "Segoe Fluent Icons") IconFont = f.Name;
    }

    public static Color Level(int pct, bool charging, bool stale)
    {
        if (stale) return Stale;
        if (charging) return Charge;
        return pct <= 15 ? Bad : pct <= 30 ? Warn : Good;
    }

    public static Font Font(float size, FontStyle style = FontStyle.Regular, string family = "Segoe UI")
    {
        string key = family + size + style;
        Font f;
        if (!fonts.TryGetValue(key, out f)) { f = new Font(family, size, style, GraphicsUnit.Point); fonts[key] = f; }
        return f;
    }
    public static Font Semibold(float size) { return Font(size, FontStyle.Regular, "Segoe UI Semibold"); }
    public static Font Icons(float size) { return Font(size, FontStyle.Regular, IconFont); }

    public static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }
    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }

    static int ReadDword(string key, string name, int def)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(key)) { object v = k == null ? null : k.GetValue(name); return v is int ? (int)v : def; } }
        catch { return def; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    // Barra de título escura, cantos arredondados e borda no Windows 11 (ignorado em versões antigas).
    public static void StyleWindow(Form f, bool rounded)
    {
        try
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);
            if (rounded) { int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4); }
            int border = Border.R | (Border.G << 8) | (Border.B << 16);
            DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
        }
        catch { }
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d > r.Height) d = r.Height;
        if (d > r.Width) d = r.Width;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using (var b = new SolidBrush(c)) using (var p = Round(r, radius)) g.FillPath(b, p);
    }

    public static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// Menu do botão direito com as cores do tema.
class ThemedRenderer : ToolStripProfessionalRenderer
{
    public ThemedRenderer() : base(new Colors()) { RoundedEdges = false; }

    class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.ControlHover; } }
        public override Color MenuItemSelected { get { return Theme.ControlHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.ControlHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.ControlHover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
        public override Color CheckBackground { get { return Theme.Control; } }
        public override Color CheckSelectedBackground { get { return Theme.ControlHover; } }
        public override Color CheckPressedBackground { get { return Theme.ControlHover; } }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Sub;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Sub;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(e.Graphics, Theme.Accent, r, 4);
        Theme.DrawText(e.Graphics, "", Theme.Icons(8), r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ======================= Componentes =======================

// Botão arredondado: Primary usa a cor de destaque.
class FlatButton : System.Windows.Forms.Control
{
    public bool Primary;
    public string Glyph;
    bool hover, down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Height = 32;
        Font = Theme.Font(9.5f);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Primary ? Theme.Accent : Theme.Control;
        if (hover) bg = Primary ? Theme.Mix(Theme.Accent, Theme.Dark ? Color.White : Color.Black, 0.1) : Theme.ControlHover;
        if (down) bg = Theme.Mix(bg, Theme.Dark ? Color.Black : Color.White, 0.15);
        Color fg = Primary ? Theme.AccentText : Theme.Text;
        if (!Enabled) { bg = Theme.Mix(bg, Theme.Bg, 0.5); fg = Theme.Sub; }
        Theme.FillRound(g, bg, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6);
        if (!Primary)
            using (var pen = new Pen(Theme.Border)) using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6)) g.DrawPath(pen, p);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
        if (!string.IsNullOrEmpty(Glyph) && string.IsNullOrEmpty(Text))
            Theme.DrawText(g, Glyph, Theme.Icons(11), ClientRectangle, fg, flags);
        else if (!string.IsNullOrEmpty(Glyph))
        {
            Size ts = TextRenderer.MeasureText(Text, Font);
            int total = 18 + ts.Width, x = (Width - total) / 2;
            Theme.DrawText(g, Glyph, Theme.Icons(10), new Rectangle(x, 0, 16, Height), fg, flags);
            Theme.DrawText(g, Text, Font, new Rectangle(x + 18, 0, ts.Width + 2, Height), fg, TextFormatFlags.VerticalCenter);
        }
        else Theme.DrawText(g, Text, Font, ClientRectangle, fg, flags);
    }
}

// Botões segmentados (ex.: 125 | 250 | 500 | 1000 Hz).
class Segmented : System.Windows.Forms.Control
{
    List<string> items = new List<string>();
    int selected = -1, hover = -1;
    public event Action Changed;

    public Segmented()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;
        Font = Theme.Font(9);
        Cursor = Cursors.Hand;
    }

    public void SetItems(IEnumerable<string> list, int sel)
    {
        items = new List<string>(list);
        selected = Math.Max(-1, Math.Min(sel, items.Count - 1));
        Invalidate();
    }
    public int Count { get { return items.Count; } }
    public int SelectedIndex
    {
        get { return selected; }
        set { if (value != selected) { selected = value; Invalidate(); } }
    }

    int At(int x) { return items.Count == 0 ? -1 : Math.Max(0, Math.Min(items.Count - 1, x * items.Count / Math.Max(1, Width))); }
    protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = At(e.X);
        if (i >= 0 && i != selected) { selected = i; Invalidate(); if (Changed != null) Changed(); }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Control, new RectangleF(0, 0, Width - 1, Height - 1), 7);
        if (items.Count == 0) return;
        float w = (float)Width / items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            var r = new RectangleF(i * w + 3, 3, w - 6, Height - 7);
            if (i == selected) Theme.FillRound(g, Theme.Accent, r, 5);
            else if (i == hover) Theme.FillRound(g, Theme.ControlHover, r, 5);
            Theme.DrawText(g, items[i], i == selected ? Theme.Semibold(9) : Font, Rectangle.Round(r),
                i == selected ? Theme.AccentText : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

// Editor de DPI: valor com − e + (passos de 50), roda do mouse e digitação.
class DpiBox : System.Windows.Forms.Control
{
    TextBox box = new TextBox();
    FlatButton minus = new FlatButton { Text = "−" }, plus = new FlatButton { Text = "+" };
    int value = 800;
    public Color Chip = Color.Red;

    public DpiBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(200, 32);
        box.BorderStyle = BorderStyle.None; box.TextAlign = HorizontalAlignment.Center;
        box.Font = Theme.Semibold(10.5f); box.BackColor = Theme.Control; box.ForeColor = Theme.Text;
        box.Leave += delegate { Commit(); };
        box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; } };
        minus.Click += delegate { Value = value - 50; };
        plus.Click += delegate { Value = value + 50; };
        Controls.Add(box); Controls.Add(minus); Controls.Add(plus);
        Layout2();
    }

    public int Value
    {
        get { Commit(); return value; }
        set { this.value = Math.Max(50, Math.Min(26000, value / 50 * 50)); box.Text = this.value.ToString(); }
    }

    void Commit()
    {
        int v;
        if (int.TryParse(box.Text.Trim(), out v)) value = Math.Max(50, Math.Min(26000, v / 50 * 50));
        box.Text = value.ToString();
    }

    protected override void OnMouseWheel(MouseEventArgs e) { Value = value + (e.Delta > 0 ? 50 : -50); base.OnMouseWheel(e); }
    protected override void OnResize(EventArgs e) { Layout2(); base.OnResize(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        box.ForeColor = Enabled ? Theme.Text : Theme.Sub; Invalidate(); base.OnEnabledChanged(e);
    }

    void Layout2()
    {
        int h = Height, chip = 22;
        minus.SetBounds(chip, 0, 30, h);
        plus.SetBounds(Width - 30, 0, 30, h);
        int bx = chip + 34, bw = Width - bx - 34;
        box.SetBounds(bx, (h - box.PreferredHeight) / 2 + 1, Math.Max(10, bw), box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Enabled ? Chip : Theme.Track)) g.FillEllipse(b, 2, Height / 2 - 7, 14, 14);
        Theme.FillRound(g, Theme.Control, new RectangleF(52, 0, Width - 52 - 32, Height - 1), 4);
    }
}

// Campo de texto com fundo arredondado e linha de destaque quando focado.
class InputBox : System.Windows.Forms.Control
{
    public TextBox Box = new TextBox();
    public InputBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;
        Box.BorderStyle = BorderStyle.None; Box.BackColor = Theme.Control; Box.ForeColor = Theme.Text; Box.Font = Theme.Font(10);
        Box.GotFocus += delegate { Invalidate(); }; Box.LostFocus += delegate { Invalidate(); };
        Controls.Add(Box);
    }
    public override string Text { get { return Box.Text; } set { Box.Text = value; } }
    protected override void OnResize(EventArgs e) { Box.SetBounds(10, (Height - Box.PreferredHeight) / 2 + 1, Width - 20, Box.PreferredHeight); base.OnResize(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Control, new RectangleF(0, 0, Width - 1, Height - 1), 6);
        using (var pen = new Pen(Theme.Border)) using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 6)) g.DrawPath(pen, p);
        if (Box.Focused) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 6, Height - 2, Width - 12, 2);
    }
}

// Seletor em lista: botão que mostra o item escolhido e abre um menu com as opções.
class Picker : FlatButton
{
    List<string> items = new List<string>();
    string selected;
    public string Placeholder = "";

    public Picker() { Glyph = null; Click += delegate { Open(); }; }

    public void SetItems(IEnumerable<string> list)
    {
        items = new List<string>(list);
        if (selected == null || !items.Contains(selected)) selected = items.Count > 0 ? items[0] : null;
        Invalidate();
    }
    public string Selected { get { return selected; } set { selected = value; Invalidate(); } }

    void Open()
    {
        if (items.Count == 0) return;
        var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), Font = Theme.Font(10) };
        foreach (string it in items)
        {
            string name = it;
            var mi = new ToolStripMenuItem(name) { Checked = name == selected };
            mi.Click += delegate { selected = name; Invalidate(); };
            m.Items.Add(mi);
        }
        m.MinimumSize = new Size(Width, 0);
        m.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Control, new RectangleF(0, 0, Width - 1, Height - 1), 6);
        using (var pen = new Pen(Theme.Border)) using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 6)) g.DrawPath(pen, p);
        string t = selected ?? Placeholder;
        Theme.DrawText(g, t, Theme.Font(10), new Rectangle(10, 0, Width - 40, Height), selected == null ? Theme.Sub : Theme.Text, TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, "", Theme.Icons(8), new Rectangle(Width - 28, 0, 20, Height), Theme.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

// Painel com fundo de cartão arredondado.
class CardPanel : Panel
{
    public string Title;
    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Card, new RectangleF(0, 0, Width - 1, Height - 1), 8);
        using (var pen = new Pen(Theme.Border)) using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 8)) g.DrawPath(pen, p);
        if (!string.IsNullOrEmpty(Title))
            Theme.DrawText(g, Title, Theme.Semibold(10.5f), new Rectangle(16, 12, Width - 32, 22), Theme.Text, TextFormatFlags.Left);
    }
}

// ======================= Painel de dispositivos =======================

class DeviceCard
{
    public string Glyph = "", Name = "", Detail = "", Status = "";
    public int Percent = -1;      // -1 = desconhecido
    public bool Charging, Stale;
}

class Flyout : Form
{
    List<DeviceCard> cards = new List<DeviceCard>();
    string subtitle = "";
    FlatButton settings = new FlatButton { Text = "Configurar mouse", Glyph = "" };
    FlatButton refresh = new FlatButton { Glyph = "" };
    FlatButton more = new FlatButton { Glyph = "" };
    Timer fade = new Timer { Interval = 15 };

    const int W = 380, Pad = 14, CardH = 84, Gap = 8, HeaderH = 64, FooterH = 60;

    public event Action SettingsClicked, RefreshClicked, MoreClicked;

    public Flyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        KeyPreview = true; BackColor = Theme.Bg; DoubleBuffered = true;
        Opacity = 0;
        settings.Click += delegate { if (SettingsClicked != null) SettingsClicked(); };
        refresh.Click += delegate { if (RefreshClicked != null) RefreshClicked(); };
        more.Click += delegate { if (MoreClicked != null) MoreClicked(); };
        Controls.Add(settings); Controls.Add(refresh); Controls.Add(more);
        fade.Tick += delegate { Opacity = Math.Min(1, Opacity + 0.15); if (Opacity >= 1) fade.Stop(); };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += delegate { Close(); };
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleWindow(this, true); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); fade.Start(); }

    public void SetCards(List<DeviceCard> list, string sub)
    {
        cards = list; subtitle = sub;
        int h = HeaderH + Math.Max(1, cards.Count) * (CardH + Gap) + FooterH;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        SetBounds(wa.Right - W - 12, wa.Bottom - h - 12, W, h);
        settings.SetBounds(Pad, h - FooterH + 14, 200, 32);
        more.SetBounds(W - Pad - 36, h - FooterH + 14, 36, 32);
        refresh.SetBounds(W - Pad - 36 - 8 - 36, h - FooterH + 14, 36, 32);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Theme.DrawText(g, "Bateria dos dispositivos", Theme.Semibold(13), new Rectangle(Pad + 2, 14, W - 2 * Pad, 26), Theme.Text, TextFormatFlags.Left);
        Theme.DrawText(g, subtitle, Theme.Font(8.5f), new Rectangle(Pad + 2, 40, W - 2 * Pad, 18), Theme.Sub, TextFormatFlags.Left);

        int y = HeaderH;
        if (cards.Count == 0)
        {
            var r = new Rectangle(Pad, y, W - 2 * Pad, CardH);
            Theme.FillRound(g, Theme.Card, r, 8);
            Theme.DrawText(g, "Nenhum dispositivo encontrado.\nO mouse está ligado?", Theme.Font(9.5f), r, Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        foreach (var c in cards) { DrawCard(g, c, new Rectangle(Pad, y, W - 2 * Pad, CardH)); y += CardH + Gap; }

        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, Height - FooterH, Width, Height - FooterH);
    }

    static void DrawCard(Graphics g, DeviceCard c, Rectangle r)
    {
        Theme.FillRound(g, Theme.Card, r, 8);
        using (var pen = new Pen(Theme.Border)) using (var p = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8)) g.DrawPath(pen, p);

        Color lvl = Theme.Level(c.Percent < 0 ? 100 : c.Percent, c.Charging, c.Stale || c.Percent < 0 && !c.Charging);

        // Ícone do dispositivo num círculo.
        var ic = new Rectangle(r.X + 14, r.Y + 18, 44, 44);
        Theme.FillRound(g, Theme.Mix(Theme.Card, lvl, Theme.Dark ? 0.18 : 0.12), ic, 22);
        Theme.DrawText(g, c.Glyph, Theme.Icons(16), ic, lvl, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int x = ic.Right + 14, right = r.Right - 14;
        string pct = c.Percent >= 0 ? c.Percent + "%" : c.Charging ? "" : "—";
        Size ps = TextRenderer.MeasureText(pct, Theme.Semibold(17));
        Theme.DrawText(g, pct, Theme.Semibold(17), new Rectangle(right - ps.Width, r.Y + 10, ps.Width + 2, 32), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (c.Charging)
            Theme.DrawText(g, "", Theme.Icons(12), new Rectangle(right - ps.Width - 22, r.Y + 10, 20, 32), Theme.Charge,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int nameW = right - x - ps.Width - (c.Charging ? 30 : 8);
        Theme.DrawText(g, c.Name, Theme.Semibold(10.5f), new Rectangle(x, r.Y + 13, nameW, 20), Theme.Text, TextFormatFlags.Left);
        Theme.DrawText(g, c.Status, Theme.Font(8.5f), new Rectangle(x, r.Y + 32, nameW, 16), c.Charging ? Theme.Charge : Theme.Sub, TextFormatFlags.Left);

        // Barra de bateria.
        var track = new RectangleF(x, r.Y + 52, right - x, 6);
        Theme.FillRound(g, Theme.Track, track, 3);
        if (c.Percent > 0) Theme.FillRound(g, lvl, new RectangleF(track.X, track.Y, Math.Max(6, track.Width * c.Percent / 100f), track.Height), 3);
        Theme.DrawText(g, c.Detail, Theme.Font(8), new Rectangle(x, r.Y + 62, right - x, 16), Theme.Sub, TextFormatFlags.Left);
    }
}

// ======================= Janela "Configurar mouse" =======================

class SettingsWindow : Form
{
    MouseSettings current;
    DpiBox[] dpi = new DpiBox[MouseSettings.MaxLevels];
    Segmented levels = new Segmented(), active = new Segmented(), polling = new Segmented(), lod = new Segmented(), peak = new Segmented();
    Picker profiles = new Picker { Placeholder = "Nenhum perfil salvo" };
    InputBox profileName = new InputBox();
    Label status = new Label();
    FlatButton apply = new FlatButton { Text = "Aplicar no mouse", Primary = true, Glyph = "" };
    List<int> peakValues = new List<int> { 30, 60, 300, 600, 900, 1800 };

    // Cores dos níveis, as mesmas do driver oficial (Config.ini).
    static readonly Color[] LevelColors = { Color.FromArgb(255, 0, 0), Color.FromArgb(0, 90, 255), Color.FromArgb(0, 200, 0),
        Color.FromArgb(255, 0, 255), Color.FromArgb(255, 210, 0), Color.FromArgb(255, 150, 0), Color.FromArgb(255, 85, 125), Color.FromArgb(0, 220, 255) };

    const int W = 560, Pad = 20;

    public SettingsWindow(MouseSettings s)
    {
        current = s;
        Text = "Configurar mouse";
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = 18;
        Controls.Add(Lbl("Configurar mouse", Theme.Semibold(16), Theme.Text, Pad, y, 400, 32));
        Controls.Add(Lbl("Redragon M916 PRO · as mudanças são gravadas direto no mouse", Theme.Font(9), Theme.Sub, Pad, y + 34, 500, 20));
        y += 70;

        // --- DPI ---
        var dpiCard = Card("DPI", y, 300);
        dpiCard.Controls.Add(Lbl("Quantidade de níveis", Theme.Font(9), Theme.Sub, 16, 46, 200, 18));
        levels.SetBounds(16, 66, W - 2 * Pad - 32, 32);
        var counts = new List<string>(); for (int i = 1; i <= MouseSettings.MaxLevels; i++) counts.Add(i.ToString());
        levels.SetItems(counts, s.Levels - 1);
        levels.Changed += delegate { UpdateLevels(); };
        dpiCard.Controls.Add(levels);
        for (int i = 0; i < MouseSettings.MaxLevels; i++)
        {
            int col = i % 2, row = i / 2, cw = (W - 2 * Pad - 48) / 2;
            dpiCard.Controls.Add(Lbl("Nível " + (i + 1), Theme.Font(8.5f), Theme.Sub, 16 + col * (cw + 16), 110 + row * 46, cw, 14));
            dpi[i] = new DpiBox { Chip = LevelColors[i], Value = s.Dpi[i] };
            dpi[i].SetBounds(16 + col * (cw + 16), 125 + row * 46, cw, 30);
            dpiCard.Controls.Add(dpi[i]);
        }
        y += 310;

        // --- nível ativo + desempenho ---
        var perf = Card("Desempenho", y, 302);
        perf.Controls.Add(Lbl("Nível de DPI ativo", Theme.Font(9), Theme.Sub, 16, 46, 200, 18));
        active.SetBounds(16, 66, W - 2 * Pad - 32, 32); perf.Controls.Add(active);
        perf.Controls.Add(Lbl("Polling (Report Rate)", Theme.Font(9), Theme.Sub, 16, 110, 200, 18));
        polling.SetBounds(16, 130, W - 2 * Pad - 32, 32);
        var polls = new List<string>(); foreach (int hz in MouseSettings.PollingOptions) polls.Add(hz + " Hz");
        polling.SetItems(polls, Math.Max(0, Array.IndexOf(MouseSettings.PollingOptions, s.Polling)));
        perf.Controls.Add(polling);
        perf.Controls.Add(Lbl("LOD (altura de rastreio)", Theme.Font(9), Theme.Sub, 16, 174, 200, 18));
        lod.SetBounds(16, 194, 200, 32); lod.SetItems(new[] { "1 mm", "2 mm" }, s.LodMm == 2 ? 1 : 0); perf.Controls.Add(lod);
        perf.Controls.Add(Lbl("Peak Performance", Theme.Font(9), Theme.Sub, 16, 238, 200, 18));
        if (!peakValues.Contains(s.PeakSeconds)) { peakValues.Add(s.PeakSeconds); peakValues.Sort(); }
        var peaks = new List<string>(); foreach (int sec in peakValues) peaks.Add(sec < 60 ? sec + " s" : (sec / 60) + " min");
        peak.SetBounds(16, 258, W - 2 * Pad - 32, 32); peak.SetItems(peaks, peakValues.IndexOf(s.PeakSeconds)); perf.Controls.Add(peak);
        y += 312;

        // --- perfis ---
        var prof = Card("Perfis", y, 128);
        profiles.SetBounds(16, 46, 250, 32);
        prof.Controls.Add(profiles);
        var load = new FlatButton { Text = "Aplicar", Glyph = "" }; load.SetBounds(276, 46, 110, 32);
        load.Click += delegate { LoadProfile(); }; prof.Controls.Add(load);
        var del = new FlatButton { Text = "Excluir", Glyph = "" }; del.SetBounds(394, 46, 110, 32);
        del.Click += delegate
        {
            if (profiles.Selected == null) return;
            Profiles.Delete(profiles.Selected); profiles.Selected = null; RefreshProfiles(); Status("Perfil excluído.", false);
        };
        prof.Controls.Add(del);
        profileName.SetBounds(16, 86, 250, 32); prof.Controls.Add(profileName);
        SetCue(profileName.Box, "Nome do novo perfil");
        var save = new FlatButton { Text = "Salvar configuração atual", Glyph = "" }; save.SetBounds(276, 86, 228, 32);
        save.Click += delegate
        {
            string name = profileName.Text.Trim();
            if (name.Length == 0) { Status("Digite um nome para o perfil.", true); return; }
            Profiles.Save(name, Collect().Serialize()); RefreshProfiles(); profiles.Selected = name; profileName.Text = "";
            Status("Perfil \"" + name + "\" salvo.", false);
        };
        prof.Controls.Add(save);
        y += 140;

        // --- rodapé ---
        status.SetBounds(Pad, y + 8, 320, 20); status.ForeColor = Theme.Sub; status.BackColor = Theme.Bg; status.Font = Theme.Font(9);
        Controls.Add(status);
        apply.SetBounds(W - Pad - 180, y, 180, 36);
        apply.Click += delegate { ApplyToMouse(Collect()); };
        Controls.Add(apply);
        y += 52;

        ClientSize = new Size(W, y);
        UpdateLevels();
        active.SelectedIndex = Math.Min(s.Active, s.Levels) - 1;
        RefreshProfiles();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleWindow(this, false); }

    CardPanel Card(string title, int y, int h)
    {
        var c = new CardPanel { Title = title };
        c.SetBounds(Pad, y, W - 2 * Pad, h);
        Controls.Add(c);
        return c;
    }

    static Label Lbl(string text, Font f, Color c, int x, int y, int w, int h)
    {
        return new Label { Text = text, Font = f, ForeColor = c, BackColor = Color.Transparent, Location = new Point(x, y), Size = new Size(w, h), AutoEllipsis = true };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    static void SetCue(TextBox t, string cue) { t.HandleCreated += delegate { SendMessage(t.Handle, 0x1501, (IntPtr)1, cue); }; }

    void Status(string text, bool error)
    {
        status.ForeColor = error ? Theme.Bad : Theme.Sub;
        status.Text = text;
    }

    void UpdateLevels()
    {
        int n = levels.SelectedIndex + 1;
        for (int i = 0; i < dpi.Length; i++) dpi[i].Enabled = i < n;
        int sel = active.SelectedIndex;
        var names = new List<string>(); for (int i = 1; i <= n; i++) names.Add(i.ToString());
        active.SetItems(names, Math.Max(0, Math.Min(sel, n - 1)));
    }

    void RefreshProfiles()
    {
        profiles.SetItems(Profiles.Names());
    }

    MouseSettings Collect()
    {
        var s = current.Copy();
        s.Levels = levels.SelectedIndex + 1;
        for (int i = 0; i < dpi.Length; i++) s.Dpi[i] = dpi[i].Value;
        s.Active = active.SelectedIndex + 1;
        s.Polling = MouseSettings.PollingOptions[polling.SelectedIndex];
        s.LodMm = lod.SelectedIndex + 1;
        s.PeakSeconds = peakValues[peak.SelectedIndex];
        return s;
    }

    void LoadProfile()
    {
        if (profiles.Selected == null) { Status("Nenhum perfil salvo ainda.", true); return; }
        var s = current.WithProfile(Profiles.Get(profiles.Selected));
        if (s == null) { Status("Perfil inválido.", true); return; }
        if (ApplyToMouse(s)) Close();
    }

    bool ApplyToMouse(MouseSettings s)
    {
        Cursor = Cursors.WaitCursor; apply.Enabled = false;
        Status("Gravando no mouse…", false); status.Refresh();
        string err = App.ApplySettings(current, s);
        Cursor = Cursors.Default; apply.Enabled = true;
        if (err == null) { Status("O mouse não respondeu. Ele está ligado?", true); return false; }
        if (err.Length > 0) { Status("Não gravou: " + err, true); return false; }
        current = s;
        Status("✓ Gravado e conferido no mouse.", false);
        status.ForeColor = Theme.Good;
        return true;
    }
}
