// SteelSeries Arctis Nova Elite (base 1038:2244), sans SteelSeries GG. Échange différent de la famille Nova 7 / Nova Pro de SteelSeriesDriver, d'où ce fichier séparé.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/steelseries_elite.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT ; niveau et charge confirmés contre SteelSeries GG sur une vraie base), d'elegos/Linux-Arctis-Manager et loteran/Arctis-Sound-Manager (nova_elite.yaml, fait d'une capture USB de SteelSeries GG).
//   requête : rapport de sortie de 64 octets 01 B0 00… vers une collection vendeur de l'interface 3 (FFC0 d'abord) ; réponse directe 01 B0 : [6] niveau du casque, [14] alimentation (01 casque éteint, 02 sur câble), [15] en charge (02) ;
//   ou trames poussées 07 B7 <niveau> <libre> <charge 02> et 07 B5 <?> <bt> <alimentation>, sur l'une ou l'autre collection. Seule la requête d'état est envoyée.
namespace BatteryTray.Providers.Hid;

public static class SteelSeriesEliteDriver
{
    const ushort Vid = 0x1038, Pid = 0x2244;

    public static byte[] BuildRequest() { var r = new byte[64]; r[0] = 0x01; r[1] = 0xB0; return r; }

    /// <summary>État cumulé lu sur les collections : niveau (-1 inconnu), alimentation (-1 inconnue), en charge.</summary>
    public sealed class State { public int Level = -1, Power = -1; public bool Charging; }

    /// <summary>Intègre un rapport (réponse directe 01 B0 ou poussées 07 B7 / 07 B5) à l'état.</summary>
    public static void Absorb(byte[] r, State st)
    {
        if (r.Length < 5) return;
        if (r[0] == 0x01 && r[1] == 0xB0 && r.Length > 15) { if (r[6] <= 100) st.Level = r[6]; st.Charging = r[15] == 0x02; st.Power = r[14]; }
        else if (r[0] == 0x07 && r[1] == 0xB7 && r[2] <= 100) { st.Level = r[2]; st.Charging = r[4] == 0x02; }
        else if (r[0] == 0x07 && r[1] == 0xB5) st.Power = r[4];
    }

    static int Rank(HidColl c) => c.Page == 0xFFC0 ? 0 : c.Page >= 0xFF00 ? 1 : 2;

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        var colls = ctx.Find(Vid, Pid, c => c.Iface == 3 && (c.InLen > 0 || c.OutLen > 0)).OrderBy(Rank).ToList(); // FFC0 d'abord, puis les autres pages vendeur (la base scinde l'interface 3 en deux collections)
        if (colls.Count == 0) return;
        var handles = new List<HidDev>();
        try
        {
            foreach (var c in colls) { var d = new HidDev(); if (d.Open(c)) { d.Flush(); handles.Add(d); } else d.Dispose(); }
            bool sent = false;
            foreach (var d in handles)
                if (d.Coll.Page >= 0xFF00 && d.Coll.OutLen > 0 && d.Write(BuildRequest())) { sent = true; break; } // une collection sans rapport 01 le refuse
            if (!sent) return;
            var st = new State();
            long end = Environment.TickCount64 + 1000;
            while (end - Environment.TickCount64 > 0 && (st.Level < 0 || st.Power < 0))
            {
                foreach (var d in handles) { var r = d.Read(30); if (r != null) Absorb(r, st); }
                if (st.Power == 0x01) break; // casque éteint
            }
            if (st.Power == 0x01 || st.Level < 0) return;
            ctx.Add(new DeviceStatus { Key = "steelseries:2244", Name = "SteelSeries Arctis Nova Elite", Kind = DeviceKind.Headset, Via = "Base station", Percent = st.Level, Charging = st.Charging || st.Power == 0x02 }, Vid, Pid);
        }
        finally { foreach (var d in handles) d.Dispose(); }
    }
}
