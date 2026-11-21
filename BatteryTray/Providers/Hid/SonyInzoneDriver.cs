// Casques Sony INZONE H9 II (054C:0FA8) et H5 (054C:0EBF), et récepteur des INZONE Buds (054C:0EC2) : une couche de commandes Sony tunnelisée dans HID (rapport 02, 64 octets) sur la collection vendeur de page FF04.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/sony_inzone.cpp, porté en C# — voir NOTICE.md.
//   commande : [02][long][01][00 FC][8+n][96 C3][adr][évènement][type][tid lo][tid hi][charge][somme]
//   évènement : [02][long][04][FF][..][00][96 C3][adr][évènement][type][tid lo][tid hi][charge][somme]
// Batterie = évènement 04, type 01 (GET) vers le casque (adr 0x41). Les deux sens portent une somme de contrôle et un identifiant de transaction.
//   Casques : charge de la réponse [en charge][%], % FF = casque éteint. Écouteurs : charge [état][%] pour l'écouteur gauche, le droit, puis le boîtier ; % FF = écouteur dans le boîtier. Avec les deux
//   écouteurs dans le boîtier, le récepteur ne répond pas. Pas d'indicateur de charge.
// Protocole : Sapd/HeadsetControl (sony_inzone_protocol.hpp, sony_inzone_buds.hpp). Disposition de la réponse des écouteurs et ordre gauche d'abord : kinako19/inzone-buds-battery-tray et takamachi66/inzone-buds-battery-tray.
namespace BatteryTray.Providers.Hid;

public static class SonyInzoneDriver
{
    const ushort Vid = 0x054C;

    public sealed record Model(ushort Pid, string Name, bool Buds, int MinPayload);
    public static readonly Model[] Models =
    {
        new(0x0FA8, "Sony INZONE H9 II", false, 2), new(0x0EBF, "Sony INZONE H5", false, 2), new(0x0EC2, "Sony INZONE Buds", true, 6),
    };

    public sealed record Event(byte Id, byte Type, ushort Tid, byte[] Payload);

    public static Event? ParseEvent(byte[] b)
    {
        if (b.Length < 15 || b[0] != 0x02) return null;
        int len = b[1];
        if (len < 12 || len > 62 || b.Length < len + 2) return null;
        if (b[2] != 0x04 || b[3] != 0xFF || b[5] != 0x00 || b[6] != 0x96 || b[7] != 0xC3) return null;
        if ((b[8] >> 4) != 0x1) return null; // doit être adressé au PC
        int sum = 0; for (int i = 5; i <= len; i++) sum += b[i];
        if ((byte)sum != b[len + 1]) return null;
        return new Event(b[9], b[10], (ushort)(b[11] | (b[12] << 8)), b[13..(len + 1)]);
    }

    public static byte[] BuildRequest(ushort tid)
    {
        var req = new byte[64];
        req[0] = 0x02; req[1] = 12; req[2] = 0x01; req[3] = 0x00; req[4] = 0xFC; req[5] = 8; req[6] = 0x96; req[7] = 0xC3;
        req[8] = 0x41; req[9] = 0x04; req[10] = 0x01; req[11] = (byte)tid; req[12] = (byte)(tid >> 8);
        int sum = 0; for (int i = 6; i <= 12; i++) sum += req[i];
        req[13] = (byte)sum;
        return req;
    }

    public static int Level(byte v) => v <= 100 ? v : -1;
    static string Pct(int v) => v >= 0 ? v + "%" : "-";

    /// <summary>Casque : (connecté, pourcentage ou -1, en charge). Charge utile [en charge][%] ; % FF = éteint.</summary>
    public static (bool Connected, int Percent, bool Charging) ParseHeadset(byte[] p) => p[1] == 0xFF ? (false, -1, p[0] != 0) : (true, p[1] <= 100 ? p[1] : -1, p[0] != 0);

    /// <summary>Écouteurs : charge utile 3 × [état][%] (gauche, droit, boîtier) → (connectés, pourcentage minimal, détail).</summary>
    public static (bool Connected, int Percent, string? Detail) ParseBuds(byte[] p)
    {
        int left = Level(p[1]), right = Level(p[3]), box = Level(p[5]);
        int pct = left < 0 || right < 0 ? Math.Max(left, right) : Math.Min(left, right);
        if (pct < 0) return (false, pct, null);
        string detail = $"L {Pct(left)}, R {Pct(right)}" + (box >= 0 ? $", case {Pct(box)}" : "");
        return (true, pct, detail);
    }

    public enum Reply { Ok, NoReply, Failed }
    static ushort _tid = 1;

    // NoReply / Failed sont identiques pour un casque ; pour les écouteurs, NoReply peut signifier que les deux sont dans le boîtier.
    static (Reply, byte[]) GetBattery(HidDev d, int minPayload)
    {
        if (++_tid <= 1) _tid = 2; // le tid 1 sert aux notifications propres du récepteur
        d.Flush();
        if (!d.Write(BuildRequest(_tid))) return (Reply.Failed, Array.Empty<byte>());
        long end = Environment.TickCount64 + 1500;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 15) return (Reply.NoReply, Array.Empty<byte>());
            if (ParseEvent(r) is not { } e || e.Id != 0x04 || e.Tid != _tid || e.Payload.Length < minPayload) continue;
            if (e.Type != 0x10 && e.Type != 0xA0) continue; // RET ou notification active
            return (Reply.Ok, e.Payload);
        }
        return (Reply.NoReply, Array.Empty<byte>());
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            DeviceStatus NewStatus() => new() { Key = "inzone:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
            bool added = false, noReply = false;
            foreach (var c in ctx.Find(Vid, m.Pid, c => c.Page == 0xFF04 && c.OutLen >= 64))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var (reply, p) = GetBattery(d, m.MinPayload);
                if (reply == Reply.NoReply) noReply = true;
                if (reply != Reply.Ok) continue;
                var s = NewStatus();
                if (m.Buds) { var b = ParseBuds(p); s.Percent = b.Percent; s.Connected = b.Connected; s.Detail = b.Detail; }
                else { var h = ParseHeadset(p); s.Connected = h.Connected; s.Percent = h.Percent; s.Charging = h.Charging; }
                ctx.Add(s, Vid, m.Pid);
                added = true;
                break;
            }
            // Écouteurs : le récepteur a pris la requête mais aucune collection n'a répondu, ce qu'il fait avec les deux écouteurs dans le boîtier : on les ajoute « non connectés » (masqués) plutôt que de prendre le récepteur pour absent.
            if (m.Buds && noReply && !added) { var s = NewStatus(); s.Connected = false; ctx.Add(s, Vid, m.Pid); }
        }
    }
}
