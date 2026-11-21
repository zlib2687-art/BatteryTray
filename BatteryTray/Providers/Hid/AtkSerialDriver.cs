// Casques ATK N9 Pro (VID 373B : récepteur 11CB, filaire 11CA) et N9 Ultra de première génération (récepteur 1204, filaire 1203). Contrairement aux autres casques, ils ne sont pas interrogés en HID mais par un port COM virtuel
// USB (9600 8N1), avec des trames de 64 octets :
//   requête [long = 2][cmd][sous-cmd][0…] ; réponse [long][cmd][sous-cmd][données…]
//   cmd 00 sous 04 = état → données[1] == 1 si le casque est connecté au récepteur ; cmd 00 sous 03 = batterie → données[1] = % (bits 0-6), charge (bit 7).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/atk_serial.cpp, porté en C# — voir NOTICE.md.
// Protocole du logiciel de configuration d'ATK (ATK HUB, modèle « n9-pro » ; ATK HUB 3.2.31 associe les identifiants du N9 Ultra au même modèle et aux mêmes réglages série, voir wushuai0802/myfinder-device-battery,
// engine/upstream-protocols.md). Le N9 Ultra donne son niveau par paliers de 10 %. Les N9 V2 plus récents utilisent un autre protocole (HID). Si l'application ATK tourne, elle tient le port et le casque est ignoré.
// Le port est retrouvé par le registre (Enum\USB\VID_373B&PID_xxxx\…\Device Parameters\PortName) ; aucune dépendance supplémentaire (accès Win32 direct).
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray.Providers.Hid;

public static class AtkSerialDriver
{
    const ushort Vid = 0x373B;

    public sealed record Model(ushort Pid, string Name, bool Wired);
    public static readonly Model[] Models = { new(0x11CB, "ATK N9 Pro", false), new(0x11CA, "ATK N9 Pro", true), new(0x1204, "ATK N9 Ultra", false), new(0x1203, "ATK N9 Ultra", true) };

    public static byte[] BuildFrame(byte cmd, byte sub) { var f = new byte[64]; f[0] = 2; f[1] = cmd; f[2] = sub; return f; }

    /// <summary>Extrait les données (61 octets après l'en-tête) de la première trame de 64 octets répondant à (cmd, sub) dans <paramref name="buf"/> ; les trames poussées spontanément sont sautées. null si absente.</summary>
    public static byte[]? FindReply(List<byte> buf, byte cmd, byte sub)
    {
        while (buf.Count >= 64)
        {
            if (buf[1] == cmd && buf[2] == sub) return buf.GetRange(3, 61).ToArray();
            buf.RemoveRange(0, 64);
        }
        return null;
    }

    public static (bool Connected, int Percent, bool Charging) Decode(byte[] status, byte[]? battery)
    {
        bool connected = status.Length > 1 && status[1] == 1;
        return connected && battery is { Length: > 1 } ? (true, HidUtil.Clamp100(battery[1] & 0x7F), (battery[1] & 0x80) != 0) : (connected, -1, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Dcb { public uint DCBlength, BaudRate, Flags; public ushort wReserved, XonLim, XoffLim; public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EvtChar; public ushort wReserved1; }
    [StructLayout(LayoutKind.Sequential)]
    struct CommTimeouts { public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref CommTimeouts t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint n, out uint written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint n, out uint read, IntPtr ov);

    sealed class Serial : IDisposable
    {
        readonly SafeFileHandle _h;
        public bool Ok { get; }

        public Serial(string port)
        {
            _h = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_READ | GENERIC_WRITE, accès exclusif
            if (_h.IsInvalid) return;
            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            GetCommState(_h, ref dcb);
            dcb.BaudRate = 9600; dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
            dcb.Flags = 1 | (1u << 4) | (1u << 12); // fBinary, DTR actif, RTS actif, pas de contrôle de flux
            var to = new CommTimeouts { ReadIntervalTimeout = 50, ReadTotalTimeoutConstant = 300, WriteTotalTimeoutConstant = 300 };
            if (!SetCommState(_h, ref dcb) || !SetCommTimeouts(_h, ref to)) return;
            PurgeComm(_h, 0x0C); // PURGE_RXCLEAR | PURGE_TXCLEAR
            Ok = true;
        }

        /// <summary>Envoie (cmd, sous) et renvoie les données de la trame de réponse correspondante.</summary>
        public byte[]? Command(byte cmd, byte sub)
        {
            var frame = BuildFrame(cmd, sub);
            if (!WriteFile(_h, frame, 64, out uint w, IntPtr.Zero) || w != 64) return null;
            var buf = new List<byte>(); var chunk = new byte[128];
            long end = Environment.TickCount64 + 800;
            while (end - Environment.TickCount64 > 0)
            {
                if (!ReadFile(_h, chunk, (uint)chunk.Length, out uint n, IntPtr.Zero)) return null;
                for (int i = 0; i < n; i++) buf.Add(chunk[i]);
                if (FindReply(buf, cmd, sub) is { } data) return data;
            }
            return null;
        }

        public void Dispose() => _h.Dispose();
    }

    /// <summary>Ports COM des produits ATK présents : (nom du port, modèle).</summary>
    static List<(string Port, Model Model)> FindPorts()
    {
        var found = new List<(string, Model)>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return found;
            foreach (var m in Models)
            {
                using var product = usb.OpenSubKey($"VID_{Vid:X4}&PID_{m.Pid:X4}");
                if (product == null) continue;
                foreach (var inst in product.GetSubKeyNames())
                {
                    using var p = product.OpenSubKey(inst + @"\Device Parameters");
                    if (p?.GetValue("PortName") is string name && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) found.Add((name, m));
                }
            }
        }
        catch { /* registre illisible : aucun port */ }
        return found;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (port, model) in FindPorts())
        {
            if (ctx.IsClaimed(Vid, model.Pid)) continue;
            using var serial = new Serial(port);
            if (!serial.Ok) continue; // occupé (appli ATK ouverte) ou port inutilisable
            if (serial.Command(0x00, 0x04) is not { } status) continue;
            var battery = status.Length > 1 && status[1] == 1 ? serial.Command(0x00, 0x03) : null;
            var (connected, pct, chg) = Decode(status, battery);
            var s = new DeviceStatus { Key = "atk-serial:" + HidUtil.Hex4(model.Pid), Name = model.Name, Kind = DeviceKind.Headset, Via = model.Wired ? "USB cable" : "2.4 GHz receiver", Connected = connected, Percent = pct, Charging = chg };
            ctx.Add(s, Vid, model.Pid);
        }
    }
}
