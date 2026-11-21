// Casque Logitech G435 LIGHTSPEED sur le récepteur récent 046D:0ADF. Il ne parle pas HID++ : sa collection vendeur (FF00:0001, rapports non numérotés
// de 64 octets) porte des messages encadrés par un CRC-16, et des commandes « PA » atteignent la puce radio du casque. La batterie se lit avec la commande
// OTA GET_BATTERY (millivolts). L'ancien récepteur 046D:0ACB utilise un autre transport et n'est pas pris en charge.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/logitech_g435_0adf.cpp et .h, porté en C# — voir NOTICE.md.
// Session (capture de portyporty) : ouverture (84 01), 85 01, initialisation (OTA IOCTL_READ_MAX_LEN), REMOTE_GET_MODE / REMOTE_SET_MODE 01 /
// REMOTE_GET_MODE pour que la requête soit transmise au casque plutôt que traitée par le récepteur, GET_BATTERY, fermeture (85 02).
// SET_MODE 01 choisit seulement où le récepteur route la commande : ce n'est pas un réglage utilisateur et il ne persiste pas.
// Protocole : github.com/portyporty/g435-battery-percentage (MIT), repris par github.com/deddroid/logitech-g435-battery.
namespace BatteryTray.Providers.Hid;

public static class LogitechG435Driver
{
    const ushort Vid = 0x046D, Pid = 0x0ADF;
    const byte CatOta = 0x06, CatWireless = 0x0E, GetMode = 0xE0, SetMode = 0xE1;

    static readonly byte[] SessionInit =
        { 0x50, 0x41, 0x06, 0x01, 0x0D, 0x00, 0x25, 0x34, 0x12, 0x5A, 0x5A, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x00 };

    /// <summary>CRC-16/XMODEM (polynôme 0x1021, valeur initiale 0).</summary>
    public static ushort Crc16(byte[] data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>Trame de 64 octets : cmd, longueur du corps, CRC (LSB, MSB), corps, complétée par des zéros. Le CRC couvre la trame entière, champ CRC à zéro.</summary>
    public static byte[] Frame(byte cmd, byte[] body)
    {
        var f = new byte[64];
        f[0] = cmd; f[1] = (byte)body.Length;
        for (int i = 0; i < body.Length && i + 4 < f.Length; i++) f[4 + i] = body[i];
        ushort crc = Crc16(f);
        f[2] = (byte)(crc & 0xFF); f[3] = (byte)(crc >> 8);
        return f;
    }

    /// <summary>Commande « PA » : 50 41 &lt;catégorie&gt; &lt;seq&gt; &lt;champ longueur&gt; &lt;charge…&gt;.</summary>
    public static byte[] Pa(byte category, byte seq, byte len, params byte[] payload) =>
        new byte[] { 0x50, 0x41, category, seq, len }.Concat(payload).ToArray();

    /// <summary>Tension de la réponse GET_BATTERY (motif 00 07 00 06 &lt;seq&gt; 00 &lt;mV lo&gt; &lt;mV hi&gt;) ; -1 si absente ou invraisemblable pour une cellule Li-Po.</summary>
    public static int ParseMillivolts(byte[] data)
    {
        for (int i = 0; i + 8 <= data.Length; i++)
            if (data[i] == 0x00 && data[i + 1] == 0x07 && data[i + 2] == 0x00 && data[i + 3] == 0x06 && data[i + 5] == 0x00)
            {
                int mv = data[i + 6] | (data[i + 7] << 8);
                if (mv is >= 2500 and <= 4500) return mv;
            }
        return -1;
    }

    static readonly int[] V = { 4200, 4150, 4100, 4050, 4000, 3950, 3900, 3850, 3800, 3750, 3700, 3650, 3600, 3550, 3500, 3400 };
    static readonly int[] P = { 100, 95, 90, 85, 80, 75, 70, 62, 55, 45, 35, 25, 15, 10, 5, 0 };

    /// <summary>Tension → pourcentage pour la cellule du G435 (courbe de portyporty), linéaire par morceaux.</summary>
    public static int Percent(int mv)
    {
        if (mv >= V[0]) return 100;
        for (int i = 1; i < V.Length; i++)
            if (mv >= V[i]) return P[i] + ((P[i - 1] - P[i]) * (mv - V[i]) + (V[i - 1] - V[i]) / 2) / (V[i - 1] - V[i]);
        return 0;
    }

    static bool Transact(HidDev d, byte cmd, byte[] body, int waitMs, out int mv)
    {
        mv = -1;
        var f = Frame(cmd, body);
        var outReport = new byte[65]; f.CopyTo(outReport, 1); // identifiant de rapport 0
        d.Flush();
        if (!d.Write(outReport)) return false;
        long end = Environment.TickCount64 + waitMs;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return true;
            var r = d.Read(left);
            if (r == null) continue;
            if ((mv = ParseMillivolts(r)) >= 0) return true;
        }
    }

    static int ReadMillivolts(HidDev d)
    {
        if (!Transact(d, 0x84, new byte[] { 0x01 }, 50, out _)) return -1;
        try
        {
            Transact(d, 0x85, new byte[] { 0x01 }, 50, out _);
            Transact(d, 0x80, SessionInit, 450, out _);
            byte seq = 2;
            byte[] Next(byte cat, byte len, params byte[] pl) { var c = Pa(cat, seq, len, pl); seq = (byte)(seq % 127 + 1); return c; }
            Transact(d, 0x80, Next(CatWireless, 1, GetMode), 120, out _);
            Transact(d, 0x80, Next(CatWireless, 2, SetMode, 0x01), 120, out _);
            Transact(d, 0x80, Next(CatWireless, 1, GetMode), 120, out _);
            Transact(d, 0x80, Next(CatOta, 1, 0x00, 0x31), 250, out int mv);
            return mv;
        }
        finally { Transact(d, 0x85, new byte[] { 0x02 }, 50, out _); } // la session est toujours refermée
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        var colls = ctx.Find(Vid, Pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.InLen == 65 && c.OutLen == 65);
        if (colls.Count == 0) return;
        using var d = new HidDev();
        if (!d.Open(colls[0])) return;
        var s = new DeviceStatus { Key = "g435:" + HidUtil.Hex4(Pid), Name = "Logitech G435", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
        int mv = ReadMillivolts(d);
        if (mv < 0) s.Connected = false; // récepteur présent, casque éteint ou hors de portée
        else { s.Detail = null; s.Percent = Percent(mv); }
        ctx.Add(s, Vid, Pid);
    }
}
