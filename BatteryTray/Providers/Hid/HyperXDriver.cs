// HyperX : casques sans fil et souris Pulsefire Haste 2 (VID HP 03F0, anciens Kingston 0951).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/hyperx.cpp, porté en C# — voir NOTICE.md.
// Sources citées par BatteryHub : Pulsefire Haste 2 Core Wireless (03F0:0AB5), Haste 2 Wireless (03F0:0F98) et Haste 2 Mini (03F0:0DA0)
// vérifiés sur matériel ; Cloud III Wireless vérifié sur matériel (03F0:0C9D) et github.com/Medprj/HyperXWM ; Cloud III S d'après
// github.com/LennardKittner/HyperHeadset ; Cloud Stinger Core d'après github.com/davidsantanaalves/HyperBatteryMonitor (aussi Cloud Core
// Wireless DTS 0951:173F, vérifié sur matériel) ; les autres suivent github.com/Sapd/HeadsetControl. Lecture seule.
namespace BatteryTray.Providers.Hid;

public static class HyperXDriver
{
    static DeviceStatus Base(ushort pid, string name) => new()
    {
        Key = "hyperx:" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver",
    };

    // ------------------------------------------------------------------ analyse des réponses (logique pure, testée)

    /// <summary>Cloud III Wireless : rapport 66 &lt;cmd&gt; ; 82 → [2] connecté, 8A → [2] en charge, 89 → [2..3] mV, [4] %.</summary>
    public static DeviceStatus? ParseCloud3(ushort pid, byte[]? connection, byte[]? charge, byte[]? battery)
    {
        if (connection == null || connection.Length < 5) return null;
        var s = Base(pid, pid is 0x069F or 0x0995 ? "HyperX Cloud II Core Wireless" : "HyperX Cloud III Wireless");
        s.Connected = connection[2] != 0;
        if (!s.Connected) return s;
        if (charge is { Length: >= 5 }) s.Charging = charge[2] == 1;
        if (battery is { Length: >= 5 } && battery[4] <= 100) s.Percent = battery[4];
        return s;
    }

    /// <summary>Cloud II Wireless (HP) : 06 FF BB &lt;cmd&gt; ; 02 → [7] %, 03 → [4] en charge.</summary>
    public static DeviceStatus? ParseCloud2(ushort pid, byte[]? battery, byte[]? charge, bool kingston)
    {
        if (battery == null || battery.Length < 8 || battery[7] > 100) return null;
        var s = Base(pid, kingston && pid is 0x16EA or 0x16EB ? "HyperX Cloud Flight S" : "HyperX Cloud II Wireless");
        s.Percent = battery[7];
        if (charge is { Length: >= 5 }) s.Charging = charge[4] == 1;
        return s;
    }

    /// <summary>Cloud Alpha Wireless : 21 BB &lt;cmd&gt; ; 03 → [3]==1 hors ligne, 0C → [3]==1 en charge, 0B → [3] %.</summary>
    public static DeviceStatus? ParseCloudAlpha(ushort pid, byte[]? link, byte[]? charge, byte[]? battery)
    {
        if (link == null || link.Length < 4) return null;
        var s = Base(pid, "HyperX Cloud Alpha Wireless");
        s.Connected = link[3] != 0x01;
        if (!s.Connected) return s;
        if (charge is { Length: >= 4 }) s.Charging = charge[3] == 0x01;
        if (battery is { Length: >= 4 } && battery[3] <= 100) s.Percent = battery[3];
        return s;
    }

    /// <summary>Cloud Alpha 2 Wireless : 52 01 → [2] connecté ; 50 02 → [2] %, [3] en charge.</summary>
    public static DeviceStatus? ParseCloudAlpha2(ushort pid, byte[]? link, byte[]? battery)
    {
        if (link == null || link.Length < 8) return null;
        var s = Base(pid, "HyperX Cloud Alpha 2 Wireless");
        s.Connected = link[2] != 0;
        if (s.Connected && battery is { Length: >= 8 } && battery[2] <= 100)
        {
            s.Percent = battery[2];
            s.Charging = battery[3] == 0x01;
        }
        return s;
    }

    /// <summary>
    /// Pulsefire Haste 2 : la même paire 52 01 / 50 02 que la Cloud Alpha 2, sur la collection vendeur du récepteur (interface 2).
    /// Pendant environ 5 s après sa mise sous tension, la souris annonce 40 % au lieu de son vrai niveau (mesuré sur une Core Wireless :
    /// 40 % pendant 2 à 5 s, puis 70 %) : une lecture dans cette fenêtre n'est pas fiable et reste inconnue.
    /// </summary>
    public static DeviceStatus? ParseHaste(ushort pid, byte[]? link, byte[]? battery, bool settled)
    {
        bool linkKnown = link is { Length: >= 8 };
        bool linked = !linkKnown || link![2] != 0;
        bool gotBattery = linked && battery is { Length: >= 8 } && battery[2] <= 100;
        if (!linkKnown && !gotBattery) return null;
        var s = Base(pid, pid == 0x0AB5 ? "HyperX Pulsefire Haste 2 Core Wireless" : pid == 0x0DA0 ? "HyperX Pulsefire Haste 2 Mini" : "HyperX Pulsefire Haste 2 Wireless");
        s.Kind = DeviceKind.Mouse;
        s.Connected = linked;
        if (gotBattery && settled) { s.Percent = battery![2]; s.Charging = battery[3] is 0x01 or 0x02; }
        return s;
    }

    /// <summary>Suit depuis quand la souris est sous tension (pour ignorer l'annonce « 40 % » du démarrage). Logique pure, testée.</summary>
    public sealed class SettleTracker
    {
        public const long SettleMs = 6000;
        long _linkUpAt;
        bool _polled;

        public bool Update(bool linked, long nowMs)
        {
            if (!linked) _linkUpAt = 0;
            else if (_linkUpAt == 0) _linkUpAt = _polled ? nowMs : nowMs - SettleMs; // premier relevé : on suppose la souris allumée depuis un moment
            _polled = true;
            return nowMs - _linkUpAt >= SettleMs;
        }
    }

    /// <summary>Cloud Flight : tension → niveau (au-dessus de 0x100B = en charge).</summary>
    public static DeviceStatus? ParseCloudFlight(ushort pid, byte[]? r)
    {
        if (r == null || r.Length < 5) return null;
        int mv = (r[3] << 8) | r[4];
        var s = Base(pid, "HyperX Cloud Flight");
        if (mv > 0x100B) s.Charging = true;
        else if (mv <= 3648) s.Percent = HidUtil.Clamp100((int)(0.00125 * mv));
        else if (mv > 3975) s.Percent = 100;
        else
        {
            double v = mv;
            s.Percent = HidUtil.Clamp100((int)Math.Round(0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3) + 2.238321 * v * v - 5706.256 * v + 5452299.0));
        }
        return s;
    }

    /// <summary>Cloud III S Wireless : 0C 02 03 01 00 &lt;cmd&gt; ; 02 → [6]==2 connecté, 06 → [6] %, 48 → [6] 0 décharge / 1 charge / 2 plein.</summary>
    public static DeviceStatus? ParseCloud3S(ushort pid, byte[]? link, byte[]? battery, byte[]? charge)
    {
        if (link == null || link.Length < 7) return null;
        var s = Base(pid, "HyperX Cloud III S Wireless");
        s.Connected = link[6] == 2;
        if (!s.Connected) return s;
        if (battery is { Length: >= 7 } && battery[6] <= 100) s.Percent = battery[6];
        if (charge is { Length: >= 7 }) s.Charging = charge[6] is 1 or 2;
        return s;
    }

    // Tension → % par paliers de 10 % (même source), courbes séparées en charge et sur batterie.
    static readonly int[] Pct = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };
    static readonly int[] ChargingMv = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
    static readonly int[] DischargingMv = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };

    /// <summary>
    /// Cloud Stinger Core Wireless (+ 7.1) : connexion → réponse [11]==1 casque allumé ; batterie → [9] 03 décharge / 05 charge / 06 plein,
    /// [11..12] mV petit-boutiste. Retourne null si la réponse de connexion est inexploitable.
    /// </summary>
    public static DeviceStatus? ParseStingerCore(ushort pid, byte[]? connection, byte[]? battery)
    {
        if (connection == null || connection.Length < 13) return null;
        var s = Base(pid, pid == 0x173F ? "HyperX Cloud Core Wireless" : pid == 0x0A8E ? "HyperX Cloud Stinger Core Wireless DTS" : "HyperX Cloud Stinger Core Wireless");
        s.Connected = connection[11] == 0x01;
        if (!s.Connected || battery == null || battery.Length < 13) return s;
        byte code = battery[9];
        if (code is not (0x03 or 0x05 or 0x06)) return s;
        int mv = battery[11] | (battery[12] << 8);
        s.Charging = code == 0x05;
        s.Percent = code == 0x06 ? 100 : HidUtil.Interpolate(mv, code == 0x05 ? ChargingMv : DischargingMv, Pct);
        return s;
    }

    // ------------------------------------------------------------------ échanges avec l'appareil

    /// <summary>Envoie <paramref name="req"/> et renvoie le premier rapport d'entrée accepté par <paramref name="match"/> dans le délai.</summary>
    static byte[]? Query(HidDev d, byte[] req, int timeoutMs, Func<byte[], bool> match)
    {
        if (!d.Write(req)) return null;
        long end = Environment.TickCount64 + timeoutMs;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null) return null;
            if (match(r)) return r;
        }
    }

    static DeviceStatus? Cloud3(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        byte[]? Q(byte cmd) => Query(d, new byte[] { 0x66, cmd }, 800, r => r.Length >= 5 && r[0] == 0x66 && r[1] == cmd);
        var link = Q(0x82);
        if (link == null) return null;
        var charge = link[2] != 0 ? Q(0x8A) : null;
        var battery = link[2] != 0 ? Q(0x89) : null;
        return ParseCloud3(c.Pid, link, charge, battery);
    }

    static DeviceStatus? Cloud2(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        byte[]? Q(byte cmd) => Query(d, new byte[] { 0x06, 0xFF, 0xBB, cmd }, 1000, r => r.Length >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == cmd);
        var battery = Q(0x02);
        return battery == null ? null : ParseCloud2(c.Pid, battery, Q(0x03), false);
    }

    static DeviceStatus? Cloud2Kingston(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        // trame fixe de 62 octets ; réponse 0B 00 BB <cmd>
        byte[]? Q(byte cmd) => Query(d, new byte[] { 0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, cmd, 0x00 },
            1000, r => r.Length >= 8 && r[0] == 0x0B && r[2] == 0xBB && r[3] == cmd);
        var battery = Q(0x02);
        return battery == null ? null : ParseCloud2(c.Pid, battery, Q(0x03), true);
    }

    static DeviceStatus? CloudAlpha(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        byte[]? Q(byte cmd) => Query(d, new byte[] { 0x21, 0xBB, cmd }, 1000, r => r.Length >= 4 && r[0] == 0x21 && r[2] == cmd);
        var link = Q(0x03);
        if (link == null) return null;
        bool on = link.Length >= 4 && link[3] != 0x01;
        return ParseCloudAlpha(c.Pid, link, on ? Q(0x0C) : null, on ? Q(0x0B) : null);
    }

    static byte[]? QueryPair(HidDev d, byte cmd, byte sub) =>
        Query(d, new byte[] { cmd, sub }, 1000, r => r.Length >= 8 && r[0] == cmd + 1 && r[1] == sub);

    static DeviceStatus? CloudAlpha2(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        var link = QueryPair(d, 0x52, 0x01);
        if (link == null) return null;
        return ParseCloudAlpha2(c.Pid, link, link[2] != 0 ? QueryPair(d, 0x50, 0x02) : null);
    }

    static readonly SettleTracker Settle = new();

    static DeviceStatus? HasteWireless(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        // Après une reconnexion, le récepteur inonde la voie de rapports sans rapport (44 xx, 45 xx, FB xx) et la réponse « lien » peut
        // manquer le délai : l'absence de réponse n'est pas une erreur, la requête batterie tranche.
        var link = QueryPair(d, 0x52, 0x01);
        bool linked = link == null || link[2] != 0;
        var battery = linked ? QueryPair(d, 0x50, 0x02) : null;
        bool settled = Settle.Update(linked, Environment.TickCount64);
        return ParseHaste(c.Pid, link, battery, settled);
    }

    static DeviceStatus? CloudFlight(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        return ParseCloudFlight(c.Pid, Query(d, new byte[] { 0x21, 0xFF, 0x05 }, 1000, r => r.Length >= 5));
    }

    static DeviceStatus? Cloud3S(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        byte[]? Q(byte cmd) => Query(d, new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, cmd }, 1000, r => r.Length >= 7 && r[0] == 0x0C && r[5] == cmd && r[6] != 0xFF);
        var link = Q(0x02);
        if (link == null) return null;
        bool on = link.Length >= 7 && link[6] == 2;
        return ParseCloud3S(c.Pid, link, on ? Q(0x06) : null, on ? Q(0x48) : null);
    }

    // Cloud Stinger Core Wireless : rapport de fonctionnalité FF sur la collection FF00:0001 (MI_03 Col03).
    // Les récepteurs PlayStation (03F0:0A93, 0951:1713) et DTS (03F0:0A8E, 0951:173F) peuvent renvoyer un rapport précédent : leur réponse
    // batterie est FF 12 05, on relit donc jusqu'à 10 × 20 ms jusqu'à ce qu'elle ait cette forme.
    static readonly byte[] StingerConnectionCmd = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00 };
    static readonly byte[] StingerBatteryCmd = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C };

    static DeviceStatus? StingerCore(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        bool ps = c.Pid is 0x0A93 or 0x1713 or 0x173F or 0x0A8E;
        byte[]? last = null;
        byte[]? Exchange(byte[] cmd, Func<byte[], bool> good)
        {
            if (!d.SetFeature(cmd)) return null;
            for (int i = 0; i < (ps ? 10 : 1); i++)
            {
                Thread.Sleep(20);
                if (!d.GetFeature(0xFF, out var r) || r.Length < 13) return null;
                last = r;
                if (!ps || good(r)) return r;
            }
            return null;
        }
        bool Fresh(byte[] r) => !(r[1] == 0x01 && r[2] == 0x00);
        bool BatteryShape(byte[] r) => r[0] == 0xFF && r[1] == 0x12 && r[2] == 0x05;

        var conn = Exchange(StingerConnectionCmd, Fresh);
        // L'en-tête de la réponse de connexion n'est pas documenté : après 10 lectures qui semblent périmées, on utilise la dernière.
        if (conn == null && !(ps && last is { Length: >= 13 })) return null;
        conn ??= last!;
        var s = ParseStingerCore(c.Pid, conn, null);
        if (s == null || !s.Connected) return s;
        var battery = Exchange(StingerBatteryCmd, BatteryShape);
        return ParseStingerCore(c.Pid, conn, battery) ?? s;
    }

    sealed record Model(ushort Vid, ushort[] Pids, Func<HidColl, bool> Accept, Func<HidColl, DeviceStatus?> Poll);

    static readonly Model[] Models =
    {
        new(0x03F0, new ushort[] { 0x05B7, 0x0C9D }, c => c.OutLen == 62 && c.InLen == 62, Cloud3),
        // Cloud II Core Wireless : même protocole 66 <cmd> que la Cloud III Wireless, rapports de 64 octets (HyperHeadset).
        new(0x03F0, new ushort[] { 0x069F, 0x0995 }, c => c.Page >= 0xFF00 && c.OutLen >= 62 && c.InLen >= 62, Cloud3),
        // Cloud III S : le rapport 0C est sur la collection 64/64 octets, que Windows range sous la page 01C0 (pas une page vendeur).
        new(0x03F0, new ushort[] { 0x06BE, 0x02CC }, c => c.OutLen == 64 && c.InLen == 64, Cloud3S),
        new(0x03F0, new ushort[] { 0x0696, 0x018B, 0x0D93, 0x0C8C }, c => c.Page == 0xFF90 && c.Usage == 0x0303, Cloud2), // 0C8C : Cloud Flight pour PS, non testé
        new(0x0951, new ushort[] { 0x1718 }, c => c.OutLen == 62, Cloud2Kingston),
        // Cloud Flight S (0951:16EA / 16EB) : même protocole en lecture seule que la Cloud II Wireless ; confirmation sur matériel en attente.
        new(0x0951, new ushort[] { 0x16EA, 0x16EB }, c => c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen == 62 && c.OutLen == 62, Cloud2Kingston),
        new(0x03F0, new ushort[] { 0x098D }, c => c.Page >= 0xFF00 && c.OutLen >= 3, CloudAlpha),
        new(0x03F0, new ushort[] { 0x08BE }, c => c.Page == 0xFF13 && c.Usage == 0xFF00, CloudAlpha2),
        // Récepteurs Pulsefire Haste 2 Wireless (0F98 testé, 0D97 non testé), Haste 2 Core Wireless (0AB5 testé) et Haste 2 Mini (0DA0 testé).
        new(0x03F0, new ushort[] { 0x0AB5, 0x0D97, 0x0F98, 0x0DA0 }, c => c.Page >= 0xFF00 && c.OutLen >= 2 && c.InLen >= 8, HasteWireless),
        new(0x0951, new ushort[] { 0x16C4, 0x1723, 0x1749 }, c => c.Page >= 0xFF00 && c.OutLen >= 3, CloudFlight),
        new(0x03F0, new ushort[] { 0x0E90 }, c => c.Page >= 0xFF00 && c.OutLen >= 3, CloudFlight), // Cloud Flight (HP)
        // Cloud Stinger Core Wireless : récepteurs PlayStation (HP 0A93, Kingston 1713), DTS HP (0A8E), Kingston d'origine, Cloud Core Wireless DTS (173F).
        new(0x03F0, new ushort[] { 0x0A93, 0x0A8E }, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen >= 32, StingerCore),
        new(0x0951, new ushort[] { 0x170B, 0x1713, 0x173F }, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen >= 32, StingerCore),
    };

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            foreach (var pid in m.Pids)
            {
                if (ctx.IsClaimed(m.Vid, pid)) continue;
                foreach (var c in ctx.Find(m.Vid, pid, m.Accept))
                {
                    var s = m.Poll(c);
                    if (s != null) { ctx.Add(s, m.Vid, pid); break; }
                }
            }
        }
    }
}
