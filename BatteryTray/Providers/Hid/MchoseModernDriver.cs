// Souris MCHOSE « nouvelles » (VID 3837) : la K7 V2 Ultra+ sur son récepteur 2,4 GHz (3837:1014) ou son câble USB (3837:4028), et les autres modèles Nordic que M HUB groupe avec le récepteur (identifiants 0x1018, 0x1016, 0x102C,
// 0x102D). Le récepteur comme le câble exposent la collection vendeur FF01:0001 (rapports de 64 octets) et répondent à la même trame « infos appareil » :
//   requête : rapport de sortie 0x4D : [4D][01][drapeaux 01 = lecture][long 00][cmd lo][cmd hi][biz][seq][XOR des octets 2..7] ; commande 0x0900 = lire les infos appareil, qui portent la batterie
//   réponse : rapport d'entrée 0x4D, même en-tête, puis long octets de données et une somme : données[0..1] = identifiant fabricant (3837), données[2..3] = identifiant du modèle appairé, données[10] = état de liaison
//   (0 non liée, 1 connectée par le câble USB, 2 liée au récepteur), données[11] = état de charge (1 = en charge), données[12] = % batterie, données[13] = mode jeu.
//   Souris éteinte, le récepteur répond quand même, mais avec long = 1 et un seul 0xFF à la place de l'identifiant du modèle (4D 01 01 01 00 09 00 00 FF 00…) : pas de données de batterie, l'appareil est donc signalé endormi
//   plutôt qu'abandonné. Vérifié sur la K7 V2 Ultra+.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose_modern.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote web M HUB de MCHOSE (cdn.mchose.com.cn, getCommandBuffer, mcReadCommand, readDeviceInfo commandId 0x0900, deviceInfoToObj), et trame documentée dans HifumiDaisuki01/MchoseBatteryTray (PROTOCOL.md).
// Confirmé sur un récepteur K7 V2 Ultra+ réel (3837:1014) : 29 % comme M HUB, et sur la même souris branchée par câble (3837:4028) : 25 % avec état de charge 1. Le pilote G3 V2 (MchoseG3Driver) parle un autre protocole sur FF01:0010 ;
// celui-ci exige FF01:0001 et le rapport 0x4D. Récepteur de clavier K87 V3 3837:1015 : même commande sur FF80:0001, charge de 20 octets et modèle appairé 2023 (rapports matériel : HOSTErss, PR #2934). Les requêtes du clavier sont
// espacées de trois minutes pour ne pas réveiller sans cesse un clavier inactif.
namespace BatteryTray.Providers.Hid;

public static class MchoseModernDriver
{
    const ushort Vid = 0x3837;
    const byte Report = 0x4D;
    const ushort CmdDeviceInfo = 0x0900;
    const int Data = 8; // premier octet du bloc de données, dans la requête comme dans la réponse

    public sealed record Target(ushort Pid, string Via, bool Keyboard = false);
    // Identifiants de récepteurs de newMouseWirelessPids de M HUB ; 0x4028 = K7 V2 Ultra+ sur câble. Seuls 0x1014 et 0x4028 ont été testés.
    public static readonly Target[] Targets =
    {
        new(0x1014, "2.4 GHz receiver"), new(0x1018, "2.4 GHz receiver"), new(0x1016, "2.4 GHz receiver"), new(0x102C, "2.4 GHz receiver"), new(0x102D, "2.4 GHz receiver"),
        new(0x4028, "USB cable"), new(0x1015, "2.4 GHz receiver", true),
    };

    public static byte Xor(byte[] b, int from, int to) { byte x = 0; for (int i = from; i <= to && i < b.Length; i++) x ^= b[i]; return x; } // bornes incluses

    public static byte[] BuildRequest(int outLen)
    {
        var req = new byte[Math.Max(outLen, 9)];
        req[0] = Report; req[1] = 0x01; req[2] = 0x01; req[4] = (byte)(CmdDeviceInfo & 0xFF); req[5] = (byte)(CmdDeviceInfo >> 8);
        req[Data] = Xor(req, 2, 7);
        return req;
    }

    /// <summary>1 = trame d'état, 0 = trame valide indiquant que la souris n'est pas liée, -1 sinon.</summary>
    public static (int State, int Percent, bool Charging) ParseInfo(byte[] r, bool keyboard)
    {
        var bad = (-1, -1, false);
        if (r.Length < Data + 2 || r[0] != Report) return bad;
        if ((ushort)(r[4] | (r[5] << 8)) != CmdDeviceInfo) return bad;
        int len = r[3];
        if (Data + len >= r.Length) return bad;
        // Souris éteinte ou endormie, le récepteur répond par l'en-tête seul, un 0xFF unique à la place de l'identifiant de modèle et une zone de données à zéro : tout ce qui est plus court que la charge de 14 octets
        // signifie « non liée » et non « mauvaise somme ».
        if (len < 14) return keyboard ? (len == 1 && r[Data] == 0xFF ? (0, -1, false) : bad) : (0, -1, false);
        if (r[Data + len] != Xor(r, 2, 7 + len)) return bad;
        if ((ushort)(r[Data] | (r[Data + 1] << 8)) != Vid) return bad;
        if (keyboard && (len < 20 || r[Data + 2] != 0x23 || r[Data + 3] != 0x20)) return bad;
        if (r[Data + 10] == 0) return (0, -1, false); // non liée : la souris est éteinte ou endormie
        if (r[Data + 12] > 100) return bad;
        return (1, r[Data + 12], r[Data + 11] == 1);
    }

    sealed class KeyboardPoll { public long NextAsk; public DeviceStatus Status = new(); }
    static readonly Dictionary<string, KeyboardPoll> Keyboards = new(); // retiré quand le récepteur disparaît

    static (int State, int Percent, bool Charging) QueryInfo(HidDev d, HidColl c, bool keyboard)
    {
        // Une seule échéance couvre écritures, délais de reprise et rapports d'entrée sans rapport, au lieu d'une seconde fraîche par reprise. Requête active : une collection muette reçoit au plus 1 s.
        long deadline = Environment.TickCount64 + 1000;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (attempt > 0) { if (left <= 150) break; Thread.Sleep(150); left = (int)(deadline - Environment.TickCount64); }
            if (left <= 0) break;
            if (!d.Write(BuildRequest(c.OutLen), Math.Min(left, 250))) continue;
            long end = Math.Min(deadline, Environment.TickCount64 + 250);
            while ((left = (int)(end - Environment.TickCount64)) > 0)
            {
                var r = d.Read(left);
                if (r == null) break;
                var info = ParseInfo(r, keyboard);
                if (info.State >= 0) return info;
            }
        }
        return (-1, -1, false);
    }

    public static void Poll(PollContext ctx)
    {
        var present = ctx.Colls.Where(c => c.Vid == Vid && c.Pid == 0x1015).Select(c => c.Path).ToHashSet();
        foreach (var k in Keyboards.Keys.Where(k => !present.Contains(k)).ToList()) Keyboards.Remove(k);
        foreach (var t in Targets)
        {
            if (ctx.IsClaimed(Vid, t.Pid)) continue;
            foreach (var c in ctx.Find(Vid, t.Pid, c => t.Keyboard ? c.Page == 0xFF80 && c.Usage == 1 && c.OutLen == 64 && c.InLen == 64 : (c.Page == 0xFF01 || c.Page == 0xFF0B) && c.OutLen >= 64 && c.InLen >= 21))
            {
                if (t.Keyboard && Keyboards.TryGetValue(c.Path, out var kp) && Environment.TickCount64 < kp.NextAsk) { ctx.Add(kp.Status, Vid, t.Pid); break; }
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                var q = QueryInfo(d, c, t.Keyboard);
                if (q.State < 0 && !t.Keyboard) continue;
                string name = c.Product.Length > 0 ? c.Product : t.Keyboard ? "MCHOSE K87 V3" : "MCHOSE mouse";
                if (t.Keyboard && name.Length > 5 && name.EndsWith(" 2.4G", StringComparison.Ordinal)) name = name[..^5];
                var s = new DeviceStatus { Key = "mchose:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(t.Pid), Name = name, Kind = t.Keyboard ? DeviceKind.Keyboard : DeviceKind.Mouse, Via = t.Via };
                if (q.State == 1) { s.Percent = HidUtil.Clamp100(q.Percent); s.Charging = q.Charging; } else s.Connected = false;
                if (t.Keyboard) Keyboards[c.Path] = new KeyboardPoll { NextAsk = Environment.TickCount64 + 3 * 60 * 1000, Status = s };
                ctx.Add(s, Vid, t.Pid);
                break;
            }
        }
    }
}
