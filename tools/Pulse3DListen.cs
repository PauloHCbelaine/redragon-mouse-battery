// Escuta (somente leitura) as coleções de entrada do dongle do PULSE 3D usando a API do Windows
// e mostra cada relatório recebido. Compile: csc /out:Pulse3DListen.exe Pulse3DListen.cs
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

class Pulse3DListen
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr p, uint d, uint f, IntPtr t);

    static void Main()
    {
        var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
        int count = 0;
        foreach (string col in new[] { "COL01", "COL02", "COL04" })
        {
            string key = "VID_054C&PID_0D5E&MI_03&" + col;
            var k = root.OpenSubKey(key);
            if (k == null) { Console.WriteLine(col + ": não encontrado"); continue; }
            foreach (string inst in k.GetSubKeyNames())
            {
                string path = @"\\?\HID#" + key + "#" + inst + "#{4d1e55b2-f16f-11cf-88cb-001111000030}";
                var h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // só leitura
                if (h.IsInvalid) { Console.WriteLine(col + "/" + inst + ": não abriu (erro " + Marshal.GetLastWin32Error() + ")"); continue; }
                count++;
                string name = col;
                var t = new Thread(() =>
                {
                    var fs = new FileStream(h, FileAccess.Read, 64, false);
                    var buf = new byte[64];
                    try
                    {
                        while (true)
                        {
                            int n = fs.Read(buf, 0, buf.Length);
                            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + name + " " + BitConverter.ToString(buf, 0, n).Replace('-', ' ') + Decode(buf, n));
                        }
                    }
                    catch (Exception e) { Console.WriteLine(name + " parou: " + e.Message); }
                });
                t.IsBackground = true; t.Start();
            }
        }
        Console.WriteLine("Escutando " + count + " coleções por 2 minutos…");
        Console.WriteLine("Faça: desligar e ligar o headset, mudar o volume, apertar o mute, ligar o cabo de carga.\n");
        Thread.Sleep(120000);
        Console.WriteLine("Fim.");
    }

    static string Decode(byte[] b, int n)
    {
        if (n < 5 || b[0] != 0xB0) return "";
        return "  -> bateria " + (b[3] == 0x80 ? "carregando" : b[3] + "%") + ", " + ((b[4] & 0x08) != 0 ? "ligado" : "desligado");
    }
}
