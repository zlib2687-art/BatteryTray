// Claviers tri-mode Semico (VID 1A2C) utilisant le protocole HID vendeur AA 55 CC 33 sur l'interface 0, page FF02 / usage 0002 (rapports de 65 octets : id 0 + charge de 64 octets).
// Couvre Attack Shark X98 (récepteur 2,4 GHz 1A2C:8FFF, câble 1A2C:A034 ; testé sur le récepteur) et AJAZZ AK820 Max Plus (récepteur 1A2C:8FFF, câble 1A2C:A036).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/semico.cpp, porté en C# — voir NOTICE.md.
//   requête [00 AA 55 CC 33 0E 00…] (requête d'état en lecture seule 0x0E) ; réponse [AA 55 CC 33 0E % état] : % 0..100 ; état 0 = hors ligne, 1..3 = en ligne.
// Les essais sur l'Attack Shark X98 en 2,4 GHz ont montré état = 1 aussi bien en charge (75 → 81 → 88 → 100 %) que sur batterie débranché (96 %) : la charge n'est donc PAS déduite de l'état 1,
// pour éviter un faux indicateur de charge permanent. Sources : shrey715/openajazz (PROTOCOL.md), AkashPanja/Kraber (KeyboardBattery.cs), captures sur Attack Shark X98 (issue #265).
namespace BatteryTray.Providers.Hid;

public static class SemicoDriver
{
    const ushort Vid = 0x1A2C, PidDongle = 0x8FFF, PidX98Cable = 0xA034, PidAk820Cable = 0xA036;

    public static bool IsKnownPid(ushort pid) => pid is PidDongle or PidX98Cable or PidAk820Cable;
    public static bool IsCablePid(ushort pid) => pid is PidX98Cable or PidAk820Cable;
    public static string? KnownModelName(ushort pid) => pid == PidX98Cable ? "Attack Shark X98" : pid == PidAk820Cable ? "AJAZZ AK820 Max Plus" : null;

    public static byte[] BuildRequest() { var r = new byte[65]; r[1] = 0xAA; r[2] = 0x55; r[3] = 0xCC; r[4] = 0x33; r[5] = 0x0E; return r; } // [0] = identifiant de rapport 0

    public enum Kind { NotThisReply, Offline, Online, Invalid }

    /// <summary>Réponse lue (identifiant 0 retiré : r[0] est le premier octet de charge).</summary>
    public static (Kind Result, int Percent) ParseReply(byte[] r)
    {
        if (r.Length < 7 || r[0] != 0xAA || r[1] != 0x55 || r[2] != 0xCC || r[3] != 0x33 || r[4] != 0x0E) return (Kind.NotThisReply, -1);
        byte pct = r[5], state = r[6];
        if (state == 0) return (Kind.Offline, -1);
        if (state > 3 || pct > 100) return (Kind.Invalid, -1);
        return (Kind.Online, pct);
    }

    public static string KeyboardName(ushort pid, string product, ushort knownCablePid)
    {
        if (KnownModelName(pid) is { } k) return k;
        if (KnownModelName(knownCablePid) is { } k2) return k2;
        return product.Length == 0 || product == "2.4 Wireless Device" || product == "USB Gaming Keyboard" ? "Wireless keyboard" : product;
    }

    static (Kind Result, int Percent) QueryStatus(HidDev d)
    {
        d.Flush();
        if (!d.Write(BuildRequest(), 500)) return (Kind.Invalid, -1);
        long end = Environment.TickCount64 + 600;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return (Kind.Invalid, -1);
            var r = d.Read(left);
            if (r == null) return (Kind.Invalid, -1);
            var p = ParseReply(r);
            if (p.Result != Kind.NotThisReply) return p;
        }
    }

    public static void Poll(PollContext ctx)
    {
        var products = ctx.Products(Vid);
        ushort knownCablePid = 0;
        foreach (var (_, pid) in products) if (IsCablePid(pid)) knownCablePid = pid;
        // Le récepteur d'abord : un clavier actif en 2,4 GHz répond aussitôt même si le câble est branché pour charger (la collection du câble expire en 2,4G).
        products = products.OrderByDescending(p => p.Pid == PidDongle).ToList();
        bool foundOnline = false; var answeredOffline = new List<ushort>();
        foreach (var (vid, pid) in products)
        {
            if (!IsKnownPid(pid)) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Iface == 0 && c.Page == 0xFF02 && c.Usage == 0x0002 && c.InLen == 65 && c.OutLen == 65))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var q = QueryStatus(d);
                if (q.Result is Kind.Invalid or Kind.NotThisReply) continue;
                if (q.Result == Kind.Offline) { answeredOffline.Add(pid); break; }
                ctx.Add(new DeviceStatus
                {
                    Key = "semico:1A2C", Name = KeyboardName(c.Pid, c.Product, knownCablePid), Kind = DeviceKind.Keyboard,
                    Via = IsCablePid(pid) ? "USB cable" : "2.4 GHz receiver", Percent = q.Percent,
                }, vid, pid);
                foundOnline = true;
                break;
            }
            if (foundOnline) break;
        }
        if (foundOnline)
        {
            foreach (var (v, pid) in products) if (IsKnownPid(pid)) ctx.Claim(v, pid); // les identifiants frères (câble branché pour charger en 2,4G)
        }
        else if (answeredOffline.Count > 0)
        {
            ctx.Out.Add(new DeviceStatus
            {
                Key = "semico:1A2C", Name = KnownModelName(knownCablePid) ?? "Wireless keyboard", Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Connected = false, Vid = Vid,
            });
            foreach (var pid in answeredOffline) ctx.Claim(Vid, pid);
        }
    }
}
