// Claviers Evision sur le récepteur 2,4 GHz partagé (320F:5088) ou le câble USB (320F:5055) : ATK V75X et claviers VGN / VXE que VGN HUB pilote de la même façon ; ATK Z87 (373B:1012).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/atk_keyboard.cpp, porté en C# — voir NOTICE.md.
// ATK V75X : porté de HaloBattery (HeyOkay, MIT ; confirmé par son auteur sur les deux), du pilote de clavier Evision d'ATK HUB 3.2.29.
// VGN / VXE : VGN HUB (drive.vgnlab.com.cn) liste ces noms sur 320F:5088 / 5055, FF1C:0092, rapport 4, type « weisheng » ; son getElectricQuantity() envoie [04][00 00][1A][06] et lit le niveau au même octet, et vérifie
// que la réponse répète les premiers octets de la requête. myfinder-device-battery (« VGN 2.4G keyboard receiver ») a lu 93 % ainsi (VGN HUB : 94 %) avec une réponse arrivant ~1 s après : on attend jusqu'à 2,5 s.
//   rapport de sortie 04 sur FF1C:0092, 31 octets de charge : [04][seq lo][seq hi][1A][00, ou 06 pour VGN][00…] ; rapport d'entrée 04 : [04][seq lo][seq hi][1A][..][00 00][..][%][état : 0 batterie, 1/2 en charge].
// Ces identifiants sont partagés par de nombreux claviers Evision : le nom du produit est donc vérifié aussi, et le clavier est nommé d'après lui (nom du récepteur sans « 2.4G Dongle »). Le récepteur est aussi vendu sous
// le nom générique « Wireless Gaming Keyboard » (Feker Alice80…) : même requête et réponse confirmées sur un Feker Alice80.
// HaloBattery ne demande par le récepteur que pendant la frappe, pour ne pas réveiller un clavier endormi. Cette appli ne surveille pas la frappe : elle demande au plus toutes les 10 minutes par le récepteur
// (à chaque lecture sur le câble, qui alimente de toute façon le clavier).
namespace BatteryTray.Providers.Hid;

public static class AtkKeyboardDriver
{
    const ushort Vid = 0x320F, Receiver = 0x5088, Cable = 0x5055, Z87Vid = 0x373B, Z87Receiver = 0x1012;
    const long ReceiverIntervalMs = 10 * 60 * 1000;

    public sealed record Model(string Product, bool Vgn);
    public static readonly Model[] Models =
    {
        new("ATK V75X 2.4G Dongle", false), new("ATK V75X", false), new("VGN S99 2.4G Dongle", true), new("VGN S99", true),
        new("VGN V98pro V2 2.4G Dongle", true), new("VGN V98pro V2", true), new("VGN V98 PRO 2.4G Dongle", true), new("VGN V98pro", true),
        new("VGN N75 PRO 2.4G Dongle", true), new("VGN N75 PRO", true), new("VGN VXE75 V2 2.4G Dongle", true), new("VGN VXE75 V2", true),
        new("VGN VXE75 2.4G Dongle", true), new("VGN VXE75", true), new("VXE V87 2.4G Dongle", true), new("VXE V87", true), new("VXE V87 RGB", true),
        new("Wireless Gaming Keyboard", false), // nom générique du récepteur (Feker Alice80…)
    };

    public static Model? FindModel(string product) => Models.FirstOrDefault(m => string.Equals(product.TrimEnd(' '), m.Product, StringComparison.OrdinalIgnoreCase));

    public static string KeyboardName(string product) =>
        product.EndsWith(" 2.4G Dongle", StringComparison.OrdinalIgnoreCase) ? product[..^" 2.4G Dongle".Length] : product;

    public static byte[] BuildRequest(ushort seq, bool vgn)
    {
        var r = new byte[32]; r[0] = 0x04; r[1] = (byte)seq; r[2] = (byte)(seq >> 8); r[3] = 0x1A; r[4] = (byte)(vgn ? 0x06 : 0x00); return r;
    }

    /// <summary>Réponse à la requête <paramref name="seq"/> : null si autre rapport ; (-1) si valeurs invalides ; sinon (%, en charge).</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r, ushort seq)
    {
        if (r.Length < 10 || r[0] != 0x04 || r[1] != (byte)seq || r[2] != (byte)(seq >> 8) || r[3] != 0x1A || r[5] != 0 || r[6] != 0) return null;
        if (r[8] > 100 || r[9] > 2) return (-1, false);
        return (r[8], r[9] == 1 || r[9] == 2);
    }

    /// <summary>Z87 : (réponse valide → (%, en charge)) ; r[3] == 0xFF d'un 373B = clavier endormi → (-2, false) ; null = autre rapport.</summary>
    public static (int Percent, bool Charging)? ParseZ87(byte[] r, ushort seq, ushort vid)
    {
        if (r.Length < 4 || r[0] != 0x04 || r[1] != (byte)seq || r[2] != (byte)(seq >> 8)) return null;
        if (vid == Z87Vid && r[3] == 0xFF) return (-2, false);
        return ParseReply(r, seq);
    }

    static ushort _seq;
    static (int Percent, bool Charging)? ReadBattery(HidColl c, bool vgn)
    {
        _seq = (ushort)(_seq % 0xFFFF + 1); ushort seq = _seq;
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(BuildRequest(seq, vgn))) return null;
        long end = Environment.TickCount64 + 2500;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read(100);
            if (r == null || ParseReply(r, seq) is not { } p) continue;
            return p.Percent < 0 ? null : p;
        }
        return null;
    }

    static ushort _z87Seq; static long _z87NextAsk; static int _z87Percent = -1; static bool _z87Charging;

    static bool ReadZ87(HidColl c)
    {
        _z87Seq = (ushort)(_z87Seq % 0xFFFF + 1);
        using var d = new HidDev();
        if (!d.Open(c)) return false;
        d.Flush();
        long end = Environment.TickCount64 + 1000;
        if (!d.Write(BuildRequest(_z87Seq, false))) return false; // HidDev complète à la longueur du rapport de sortie (64 sur le Z87)
        for (; ; )
        {
            long now = Environment.TickCount64;
            if (now >= end) return false;
            var r = d.Read((int)(end - now));
            if (r == null) return false;
            if (ParseZ87(r, _z87Seq, c.Vid) is not { } p) continue;
            if (p.Percent < 0) return false;
            _z87Percent = p.Percent; _z87Charging = p.Charging;
            return true;
        }
    }

    static void PollZ87(PollContext ctx)
    {
        if (ctx.IsClaimed(Z87Vid, Z87Receiver)) return;
        foreach (var c in ctx.Find(Z87Vid, Z87Receiver, c => c.Page == 0xFF1C && c.Usage == 0x0092 && c.InLen == 64 && c.OutLen == 64))
        {
            if (!string.Equals(c.Product.TrimEnd(' '), "ATK Z87 Dongle", StringComparison.OrdinalIgnoreCase)) continue;
            long now = Environment.TickCount64;
            if (now >= _z87NextAsk)
            {
                _z87NextAsk = now + ReceiverIntervalMs;
                if (!ReadZ87(c)) { _z87Percent = -1; _z87Charging = false; _z87NextAsk = now + 30_000; }
            }
            var s = new DeviceStatus { Key = "atk_keyboard:1012", Name = "ATK Z87", Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Percent = _z87Percent, Charging = _z87Charging };
            if (s.Percent < 0) s.Connected = false;
            ctx.Add(s, Z87Vid, Z87Receiver);
            return;
        }
        _z87Seq = 0; _z87NextAsk = 0; _z87Percent = -1; _z87Charging = false; // une reconnexion n'hérite pas d'une ancienne lecture du récepteur
    }

    static long _lastAsk; static int _lastPercent = -1; static bool _lastCharging;

    public static void Poll(PollContext ctx)
    {
        PollZ87(ctx);
        foreach (ushort pid in new[] { Cable, Receiver }) // le câble l'emporte quand les deux sont branchés
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            bool cable = pid == Cable;
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFF1C && c.Usage == 0x0092))
            {
                var model = FindModel(c.Product);
                if (model == null) continue;
                var s = new DeviceStatus { Key = "atk_keyboard:5088", Name = KeyboardName(model.Product), Kind = DeviceKind.Keyboard, Via = cable ? "USB cable" : "2.4 GHz receiver" }; // une entrée pour le clavier sur l'une ou l'autre connexion
                long now = Environment.TickCount64;
                bool ask = cable || _lastAsk == 0 || now - _lastAsk >= ReceiverIntervalMs;
                (int Percent, bool Charging)? got = null;
                if (ask)
                {
                    _lastAsk = now;
                    got = ReadBattery(c, model.Vgn);
                    if (got is { } g) { _lastPercent = g.Percent; _lastCharging = g.Charging; }
                }
                if (ask && got == null) s.Connected = false; // pas de réponse : endormi, éteint ou occupé ; on garde la dernière valeur
                else
                {
                    s.Percent = ask ? got!.Value.Percent : _lastPercent;
                    s.Charging = ask ? got!.Value.Charging : _lastCharging;
                    if (s.Percent < 0) s.Connected = false;
                }
                ctx.Add(s, Vid, pid);
                if (cable) ctx.Claim(Vid, Receiver);
                return;
            }
        }
    }
}
