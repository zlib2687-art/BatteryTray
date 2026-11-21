// Casques sans fil Logitech / ASTRO (A50 X, A50 Gen 5, A50 Gen 4, A50 Gen 3, A20).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/astro.cpp, porté en C# — voir NOTICE.md.
//
// 1. ASTRO A50 X (046D:0B0B) et A50 Gen 5 (046D:0B1C) : collection vendeur FF32 / usage 0074 (interface 8), rapports de 64 octets, id 02 :
//      requête : [02][0C][03][00][06][0C][00…]
//      réponse : [02][0C][len][00][06][handle][pourcentage][…][en charge]       (protocole : Sapd/HeadsetControl logitech_astro_a50.hpp)
// 2. ASTRO A50 Gen 4 (« Mimas »), A50 Gen 3 (« Europa »), A20 (« Aquarius »), VID 9886 : stations 002C/002D/002A (Gen 4), 0015/0014/0018/001D/0026
//    (Gen 3), émetteurs A20 0037/0038/0039/0043/0044/0045. Collection vendeur FF32 ou FF31, usage 0074 (interface 6), rapports de 64 octets, id 02 :
//      requête [02][cmd] ; réponse [02][état][len][données…] (état 02 = OK, 00 = accepté, 01 = erreur)
//    cmd 0x54 (état du casque) → données[0] : bit 0 = posé sur la base, bit 1 = liaison 2,4 GHz active
//    cmd 0x7C (batterie)       → données[0] : bit 7 = en charge, bits 0..6 = % (0..100)
//    (protocole : tdryer/eh-fifty, nichtlegacy/AstroBar, Sapd/HeadsetControl logitech_astro_a50_gen4.hpp.)
//    L'A50 Gen 3 (9886:0015, base TXDP01) répond à 0x54 mais refuse 0x7C : sa batterie est dans la lecture « interface courante » :
//    requête [02][72][01][01], quatre groupes [02][champ][valeur][00] ; le groupe du champ 04 (données[12..15]) donne le % (bits 0..6) et la
//    charge (bit 7). Sur Gen 4, 0x72 a un autre sens : il n'est utilisé qu'après l'échec de 0x7C (OpenAstroCC docs/A50-PROTOCOL.md, testé sur 0015).
//    Coexistence avec Astro Command Center (UWP) : l'application ouvre FF32:0074 en lecture/écriture avec un verrou exclusif ; le casque reste alors
//    visible avec sa dernière lecture, et la lecture reprend dès que l'application est fermée. Lecture seule : jamais de réglage audio ni de casque.
namespace BatteryTray.Providers.Hid;

public static class AstroDriver
{
    const ushort VidAstro = 0x9886, VidLogitech = 0x046D;

    static readonly Dictionary<ushort, string> LegacyModels = new()
    {
        [0x002C] = "ASTRO A50 Gen 4", [0x002D] = "ASTRO A50 Gen 4", [0x002A] = "ASTRO A50 Gen 4",
        [0x0015] = "ASTRO A50 Gen 3", [0x0014] = "ASTRO A50 Gen 3", [0x0018] = "ASTRO A50 Gen 3", [0x001D] = "ASTRO A50 Gen 3", [0x0026] = "ASTRO A50 Gen 3",
        [0x0037] = "ASTRO A20 Wireless", [0x0038] = "ASTRO A20 Wireless", [0x0039] = "ASTRO A20 Wireless",
        [0x0043] = "ASTRO A20 Wireless", [0x0044] = "ASTRO A20 Wireless", [0x0045] = "ASTRO A20 Wireless",
    };

    public enum Framing { Unknown, ReportId02, NoReportId }
    public enum QResult { Ok, Error, NoReply, WriteFailed }

    // ------------------------------------------------------------------ trames et analyse (logique pure, testée)

    /// <summary>Trame [02][cmd] (+ longueur et données) ; certains micrologiciels Gen 3 ne numérotent pas leurs rapports : un 00 précède alors la trame.</summary>
    public static byte[] LegacyFrame(Framing f, byte cmd, byte[]? payload = null)
    {
        var v = new List<byte>();
        if (f == Framing.NoReportId) v.Add(0x00);
        v.Add(0x02); v.Add(cmd);
        if (payload is { Length: > 0 }) { v.Add((byte)payload.Length); v.AddRange(payload); }
        return v.ToArray();
    }

    /// <summary>Classe un rapport reçu : null = à ignorer ; (Error) = refusé ; (Ok, trame) = réponse valide [02][02][len]….</summary>
    public static (QResult Result, byte[]? Frame)? ClassifyLegacyReply(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x02) return null;
        if (r[1] == 0x01) return (QResult.Error, null);   // compris mais refusé (commande inconnue / pas de liaison)
        if (r[1] == 0x02) return (QResult.Ok, r);
        return null;
    }

    /// <summary>Premier octet de données d'une réponse valide (cmd 0x54 ou 0x7C).</summary>
    public static byte LegacyValue(byte[] reply) => reply[3];

    /// <summary>État du casque (cmd 0x54) : (posé sur la base, liaison 2,4 GHz active).</summary>
    public static (bool Docked, bool Linked) ParseStatus(byte st) => ((st & 0x01) != 0, (st & 0x02) != 0);

    /// <summary>Batterie Gen 3 (lecture 0x72) : données[12..15] = [02][04][batterie][00] ; null si la trame n'a pas cette forme.</summary>
    public static byte? ParseGen3Battery(byte[] r)
    {
        if (r.Length < 3 + 16 || (r[2] & 0x3F) < 16) return null;
        var data = r.AsSpan(3);
        if (data[12] != 0x02 || data[13] != 0x04 || (data[14] & 0x7F) > 100) return null;
        return data[14];
    }

    /// <summary>Résultat d'une lecture de batterie (octet cmd 0x7C ou Gen 3) : (pourcentage, en charge).</summary>
    public static (int Percent, bool Charging)? ParseBatteryByte(byte bat, bool docked)
    {
        if ((bat & 0x7F) > 100) return null;
        int pct = bat & 0x7F;
        return (pct, (bat & 0x80) != 0 || (docked && pct < 100));
    }

    public static byte[] BuildA50XRequest()
    {
        var request = new byte[64];
        request[0] = 0x02; request[1] = 0x0C; request[2] = 3; request[4] = 0x06; request[5] = 0x0C;
        return request;
    }

    /// <summary>
    /// Réponse A50 X / Gen 5 : (pourcentage, en charge) ou null. Ignore les évènements non sollicités (volume, mixage) et les trames mal formées ou partielles.
    /// <paramref name="count"/> = nombre d'octets réellement lus.
    /// </summary>
    public static (int Percent, bool Charging)? ParseA50X(byte[] reply, int count)
    {
        if (count < 9 || reply.Length < 9 || reply[0] != 0x02 || reply[1] != 0x0C || reply[2] < 6 || (long)reply[2] + 3 > reply.Length
            || reply[2] + 3 > count || reply[3] != 0 || reply[4] != 0x06 || (reply[5] != 0 && reply[5] != 0x0C) || reply[6] > 100) return null;
        return (reply[6], reply[8] != 0);
    }

    // ------------------------------------------------------------------ échanges avec l'appareil

    sealed record Cached(int Percent, bool Charging, bool Docked);
    static readonly Dictionary<ushort, Cached> Cache = new();
    static readonly Dictionary<ushort, Framing> Framings = new();

    static (QResult, byte[]?) QueryOnce(HidDev d, Framing f, byte cmd, byte[]? payload)
    {
        d.Flush();
        if (!d.Write(LegacyFrame(f, cmd, payload))) return (QResult.WriteFailed, null);
        long end = Environment.TickCount64 + 500;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 4) break;
            var c = ClassifyLegacyReply(r);
            if (c == null) continue;
            return c.Value;
        }
        return (QResult.NoReply, null);
    }

    static (QResult Result, byte[]? Reply) QueryLegacy(HidDev d, ushort pid, byte cmd, byte[]? payload = null)
    {
        var known = Framings.TryGetValue(pid, out var k) ? k : Framing.Unknown;
        var order = known == Framing.NoReportId ? new[] { Framing.NoReportId, Framing.ReportId02 } : new[] { Framing.ReportId02, Framing.NoReportId };
        (QResult, byte[]?) last = (QResult.NoReply, null);
        foreach (var f in order)
        {
            last = QueryOnce(d, f, cmd, payload);
            if (last.Item1 is QResult.Ok or QResult.Error) { Framings[pid] = f; return last; }
            if (known != Framing.Unknown) break; // cadrage déjà prouvé : un échec est un vrai échec
        }
        return last;
    }

    static (QResult Result, byte Value) QueryByte(HidDev d, ushort pid, byte cmd)
    {
        var (res, reply) = QueryLegacy(d, pid, cmd);
        return (res, res == QResult.Ok && reply != null ? LegacyValue(reply) : (byte)0);
    }

    static DeviceStatus? PollLegacyCollection(HidColl c, string name)
    {
        var s = new DeviceStatus { Key = "astro:" + HidUtil.Hex4(c.Pid), Name = name, Kind = DeviceKind.Headset, Via = "USB base station" };
        using var d = new HidDev();
        if (d.Open(c))
        {
            // 0x54 (état dock / liaison) est facultatif : certains micrologiciels Gen 3 n'y répondent pas.
            var (rs, st) = QueryByte(d, c.Pid, 0x54);
            bool haveStatus = rs == QResult.Ok;
            var (docked, linked) = haveStatus ? ParseStatus(st) : (false, false);
            if (haveStatus)
            {
                s.Connected = docked || linked;
                if (!s.Connected) return s;
            }
            var (rb, bat) = QueryByte(d, c.Pid, 0x7C);
            if (rb == QResult.Error && haveStatus)
            {
                var (g3, reply) = QueryLegacy(d, c.Pid, 0x72, new byte[] { 0x01 }); // A50 Gen 3
                if (g3 == QResult.Ok && reply != null && ParseGen3Battery(reply) is byte g3Battery) { bat = g3Battery; rb = QResult.Ok; }
            }
            if (rb == QResult.Ok && ParseBatteryByte(bat, docked) is { } parsed)
            {
                s.Connected = true; s.Percent = parsed.Percent; s.Charging = parsed.Charging;
                if (docked) s.Detail = "docked";
                Cache[c.Pid] = new Cached(s.Percent, s.Charging, docked);
                return s;
            }
            if (rb == QResult.Error && !haveStatus) { s.Connected = false; return s; } // la base répond : casque éteint ou hors de portée
            return haveStatus ? s : null;
        }

        // Verrou exclusif tenu par Astro Command Center (UWP) : le casque reste visible avec sa dernière lecture.
        s.Connected = true;
        if (Cache.TryGetValue(c.Pid, out var hit) && hit.Percent >= 0)
        {
            s.Percent = hit.Percent; s.Charging = hit.Charging;
            s.Detail = hit.Docked ? "docked, Astro Command Center" : "Astro Command Center";
        }
        else s.Detail = "Astro Command Center";
        return s;
    }

    static bool IsLegacyVendorColl(HidColl c) => (c.Page is 0xFF32 or 0xFF31 or 0xFF00) && c.InLen >= 64 && c.OutLen >= 64;

    public static void Poll(PollContext ctx)
    {
        // 1. ASTRO A50 X (046D:0B0B) et A50 Gen 5 (046D:0B1C)
        foreach (var c in ctx.Colls.ToList())
        {
            if (c.Vid != VidLogitech || (c.Pid != 0x0B0B && c.Pid != 0x0B1C) || ctx.IsClaimed(c.Vid, c.Pid) || c.Page != 0xFF32 || c.Usage != 0x0074 || c.InLen != 64 || c.OutLen != 64) continue;
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(BuildA50XRequest())) continue; // aucune donnée en plus : lecture de la batterie seule
            long deadline = Environment.TickCount64 + 1000;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) break;
                var reply = d.Read((int)left);
                if (reply == null) break;
                if (ParseA50X(reply, reply.Length) is not { } p) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = ctx.StableKey("astro:" + HidUtil.Hex4(c.Pid)),
                    Name = c.Pid == 0x0B0B ? "ASTRO A50 X" : "ASTRO A50 Gen 5",
                    Kind = DeviceKind.Headset, Via = "USB base station", Percent = p.Percent, Charging = p.Charging,
                }, c.Vid, c.Pid);
                break;
            }
        }

        // 2. ASTRO A50 Gen 4 / Gen 3 et A20 Wireless (VID 9886)
        foreach (var (pid, name) in LegacyModels)
        {
            if (ctx.IsClaimed(VidAstro, pid)) continue;
            foreach (var c in ctx.Find(VidAstro, pid, c => IsLegacyVendorColl(c) || (c.Page == 0 && (c.Iface == 6 || c.Iface == -1))))
            {
                var s = PollLegacyCollection(c, name);
                if (s != null) { ctx.Add(s, VidAstro, pid); break; }
            }
        }
    }
}
