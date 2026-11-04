// Mouses AJAZZ da família Telink (AJ139 / AJ159 / AJ179, incluindo AJ139 V2 PRO e AJ179 V2 MAX).
//
// Protocolo (só leitura), confirmado em hardware no AJ179 V2 MAX por HaloBattery (PR #246), a partir
// de capturas do app oficial "AJAZZ Driver 1.0.7.3". O AJ139 V2 PRO usa o mesmo chassi e os mesmos
// IDs USB segundo o config.xml do driver oficial (documentado em Aiacos/ajazz-control-center).
//
//   Dispositivos: dongle 249A:5C2F ou 248A:5C2F; mouse no cabo 248A:5C2E / 5D2E / 5E2E.
//   Canal: interface 2 (MI_02), a coleção que aceita relatório de saída de 33 bytes.
//   Pedido:   00 10 00 00 … (33 bytes; o 00 inicial é o report id)  — o "info" que o app oficial lê.
//   Resposta: 10 00 01 0B … [13]=bateria %  [14]=00 se carregando  [31]=soma dos bytes 4..30 (8 bits)
//   Aviso espontâneo do dongle: C0 01 <bateria %>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

class AjazzReading
{
    public bool Present;    // dongle ou mouse no cabo conectado
    public bool Ok;         // conseguiu ler a bateria
    public int Percent;
    public bool Charging;   // só vale no cabo: sem fio, o mouse nunca está carregando
    public bool Wired;
    public DateTime Time = DateTime.Now;
}

static class Ajazz
{
    static readonly string[] DongleIds = { "vid_249a&pid_5c2f", "vid_248a&pid_5c2f" };
    static readonly string[] WiredIds = { "vid_248a&pid_5c2e", "vid_248a&pid_5d2e", "vid_248a&pid_5e2e" };
    const int ReportSize = 32, OutLen = 33;

    static List<string> Channels(string[] ids)
    {
        var list = new List<string>();
        foreach (string p in Hid.FindPaths("mi_02"))
            foreach (string id in ids) if (p.Contains(id)) { list.Add(p); break; }
        // A coleção com relatório de saída de 33 bytes é o canal do fabricante: vem primeiro.
        list.Sort((a, b) => Rank(a) - Rank(b));
        return list;
    }

    static int Rank(string path)
    {
        Hid.Caps c = Hid.GetCaps(path);
        if (c == null) return 1;
        return c.OutLen == OutLen ? 0 : c.OutLen == 0 ? 3 : 2;
    }

    public static bool AnyPresent()
    {
        foreach (string p in Hid.FindPaths("mi_02"))
            foreach (string id in DongleIds) if (p.Contains(id)) return true;
        foreach (string p in Hid.FindPaths("mi_02"))
            foreach (string id in WiredIds) if (p.Contains(id)) return true;
        return false;
    }

    public static AjazzReading Read(List<string> log = null)
    {
        var r = new AjazzReading();
        var dongles = Channels(DongleIds);
        var wired = Channels(WiredIds);
        r.Present = dongles.Count > 0 || wired.Count > 0;
        if (!r.Present) { Log(log, "Nenhum AJAZZ (248A/249A) encontrado."); return r; }

        // Primeiro pelo dongle; se o rádio estiver desligado (mouse no cabo), pelo próprio mouse.
        foreach (var group in new[] { dongles, wired })
        {
            bool isWired = group == wired;
            int tried = 0;
            foreach (string p in group)
            {
                if (tried++ >= 4) break;
                Log(log, (isWired ? "[cabo] " : "[dongle] ") + p + Describe(p));
                bool accepted;
                int pct; bool? charging;
                if (Ask(p, log, out accepted, out pct, out charging))
                {
                    r.Ok = true; r.Percent = pct; r.Wired = isWired;
                    r.Charging = isWired && charging == true;
                    return r;
                }
                if (accepted) break; // canal certo, mas o mouse não respondeu (dormindo/desligado)
            }
        }
        return r;
    }

    // Envia o pedido de leitura e escuta ~1,2 s. accepted = a coleção aceitou o pedido.
    static bool Ask(string path, List<string> log, out bool accepted, out int pct, out bool? charging)
    {
        accepted = false; pct = 0; charging = null;
        Hid.Caps caps = Hid.GetCaps(path);
        if (caps != null && caps.OutLen == 0) { Log(log, "    sem relatório de saída, pulando"); return false; }
        try
        {
            using (SafeFileHandle h = Hid.Open(path))
            {
                if (h.IsInvalid) { Log(log, "    não abriu"); return false; }
                int outLen = caps != null && caps.OutLen > 0 ? caps.OutLen : OutLen;
                int inLen = caps != null && caps.InLen > 0 ? caps.InLen : OutLen;
                using (var fs = new FileStream(h, FileAccess.ReadWrite, Math.Max(outLen, inLen), true))
                {
                    var req = new byte[outLen];
                    req[0] = 0x00; req[1] = 0x10;
                    try { fs.Write(req, 0, req.Length); }
                    catch (Exception e) { Log(log, "    pedido recusado: " + e.Message); return false; }
                    accepted = true;

                    int annPct = 0;
                    DateTime end = DateTime.Now.AddMilliseconds(1250);
                    IAsyncResult pending = null; byte[] buf = null;
                    while (DateTime.Now < end)
                    {
                        if (pending == null) { buf = new byte[inLen]; pending = fs.BeginRead(buf, 0, buf.Length, null, null); }
                        TimeSpan left = end - DateTime.Now;
                        if (left <= TimeSpan.Zero || !pending.AsyncWaitHandle.WaitOne(left)) break;
                        int n = fs.EndRead(pending); pending = null;
                        string kind; int lvl; bool? flag;
                        bool parsed = Parse(buf, n, out kind, out lvl, out flag);
                        Log(log, "    resposta: " + Hex(buf, n) + (parsed ? "  -> " + lvl + "% (" + kind + ")" : "  (sem bateria)"));
                        if (!parsed) continue;
                        if (kind == "info") { pct = lvl; charging = flag; return true; }
                        annPct = lvl; // aviso sem o indicador de carga: guarda e espera o "info"
                    }
                    if (annPct > 0) { pct = annPct; return true; }
                    Log(log, "    silêncio (mouse dormindo ou desligado)");
                    return false;
                }
            }
        }
        catch (Exception e) { Log(log, "    erro: " + e.Message); return false; }
    }

    public static bool Parse(byte[] raw, int n, out string kind, out int level, out bool? charging)
    {
        kind = null; level = 0; charging = null;
        int off = (n >= ReportSize + 1 && raw[0] == 0x00) ? 1 : 0; // o Windows devolve o report id na frente
        if (n - off < ReportSize) return false;
        byte b0 = raw[off], b1 = raw[off + 1], b2 = raw[off + 2];
        if (b0 == 0xC0)
        {
            if (b1 != 0x01 || b2 == 0 || b2 > 100) return false;
            kind = "aviso"; level = b2; return true;
        }
        if (b0 != 0x10 || b1 != 0x00 || b2 != 0x01 || raw[off + 3] != 0x0B) return false;
        int sum = 0; for (int i = 4; i <= 30; i++) sum += raw[off + i];
        if ((sum & 0xFF) != raw[off + 31]) return false;
        int lv = raw[off + 13];
        if (lv == 0 || lv > 100) return false;
        kind = "info"; level = lv; charging = raw[off + 14] == 0x00;
        return true;
    }

    static string Describe(string path)
    {
        Hid.Caps c = Hid.GetCaps(path);
        return c == null ? "" : string.Format("  (page {0:X4}:{1:X4}, in {2}, out {3}, feature {4})", c.UsagePage, c.Usage, c.InLen, c.OutLen, c.FeatLen);
    }

    static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n && i < 40; i++) sb.Append(b[i].ToString("x2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    static void Log(List<string> log, string s) { if (log != null) log.Add(s); }
}
