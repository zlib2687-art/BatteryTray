// Finalmouse UltralightX (ULX) et Starlight X (SLX) sur leurs récepteurs 2,4 GHz (VID 361D).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/finalmouse.cpp, porté en C# — voir NOTICE.md.
// Protocole de xpanel (configurateur web de Finalmouse), repris par OpenMouse (mouse-protocol) et FinalmousePollingRateSwitcher.
// Trames sur la collection vendeur (page FF00), complétées par des zéros : [id rapport][longueur interne = 2 + n][0x80 | commande][n][n octets].
// Une écriture sans charge est un GET ; les réponses reviennent en rapports d'entrée avec la commande nue.
//  * Récepteur ULX 0100 (FF00:0001) : sortie 04, réponses en entrée 05. Récepteurs SLX 0300 / 0301 : sortie 01, réponses 02.
// Seuls des GET sont envoyés : la lecture par laquelle xpanel commence (ULX lift-off 21, SLX liste des profils 57), puis wake/all (96), qui fait pousser
// au récepteur sa rafale d'état : 5 = tension (mV, u16 LE), 38 = état de charge [%][mV u16] (préféré), 37 = charge (1 = en charge), 36 = liaison (0 = aucune).
// La souris sur son propre câble (361D:0102, 0310, 0311) n'est pas prise en charge : ses identifiants de rapport sont inconnus.
namespace BatteryTray.Providers.Hid;

public static class FinalmouseDriver
{
    const ushort Vid = 0x361D;
    const byte Voltage = 5, Link = 36, Charging = 37, StateOfCharge = 38, WakeAll = 96;

    public sealed record Model(ushort Pid, string Name, bool Slx, byte OutId, byte InId, byte FirstGet);
    public static readonly Model[] Models =
    {
        new(0x0100, "Finalmouse UltralightX", false, 0x04, 0x05, 21),
        new(0x0300, "Finalmouse Starlight X", true, 0x01, 0x02, 57),
        new(0x0301, "Finalmouse Starlight X", true, 0x01, 0x02, 57),
    };

    static readonly int[] Mv = { 3000, 3620, 3660, 3740, 3880, 4170, 4380 };
    static readonly double[] Pct = { 0.2, 5, 10, 25, 50, 75, 100 };

    /// <summary>Courbe de tension de xpanel : linéaire entre les points d'étalonnage, arrondie au pourcent.</summary>
    public static int PercentFromMillivolts(int mv)
    {
        if (mv <= Mv[0]) return 0;
        for (int i = 1; i < Mv.Length; i++)
        {
            if (mv > Mv[i]) continue;
            double f = (double)(mv - Mv[i - 1]) / (Mv[i] - Mv[i - 1]);
            return (int)Math.Floor(Pct[i - 1] + (Pct[i] - Pct[i - 1]) * f + 0.5);
        }
        return 100;
    }

    public sealed class Status { public int Soc = -1, Mv, Charging = -1, Link = -1; }

    static bool ValidMv(int mv) => mv > 0 && mv < 5000;

    /// <summary>Intègre un rapport d'entrée à l'état (les rapports d'un autre identifiant, trop courts ou inconnus sont ignorés).</summary>
    public static void Decode(byte[] r, byte inId, Status st)
    {
        if (r.Length < 4 || r[0] != inId) return;
        int inner = Math.Min(r[1], r.Length - 2);
        if (inner < 2) return;
        int n = Math.Min(r[3], inner - 2);
        int p(int i) => r[4 + i];
        if (4 + n > r.Length) return;
        switch (r[2])
        {
            case Voltage: if (n >= 2 && ValidMv(p(0) | p(1) << 8)) st.Mv = p(0) | p(1) << 8; break;
            case StateOfCharge:
                if (n >= 3 && p(0) <= 100) { st.Soc = p(0); if (ValidMv(p(1) | p(2) << 8)) st.Mv = p(1) | p(2) << 8; }
                break;
            case Charging: if (n >= 1) st.Charging = p(0); break;
            case Link: if (n >= 1) st.Link = p(0); break;
        }
    }

    public static byte[] BuildGet(byte reportId, byte command) => new byte[] { reportId, 2, (byte)(0x80 | command), 0 };

    static bool Query(HidDev d, Model m, Status st)
    {
        if (!d.Write(BuildGet(m.OutId, m.FirstGet)) || !d.Write(BuildGet(m.OutId, WakeAll))) return false;
        // xpanel attend jusqu'à 1 s la rafale. Le niveau reçu, la charge suit en général dans la même rafale : on ne l'attend que brièvement.
        // Le pourcentage propre du SLX (38) est préféré à la tension : on continue de l'attendre.
        long end = Environment.TickCount64 + 1000; bool shortened = false;
        for (; ; )
        {
            bool level = st.Soc >= 0 || (!m.Slx && st.Mv > 0);
            if (level && st.Charging >= 0) break;
            if (level && !shortened) { end = Math.Min(end, Environment.TickCount64 + 150); shortened = true; }
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) break;
            var r = d.Read(left);
            if (r == null) break;
            Decode(r, m.InId, st);
        }
        return true;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFF00 && (m.Slx || c.Usage == 1) && c.InLen >= 4 && c.OutLen >= 4)
                .OrderByDescending(c => c.Usage == 1).ToList(); // l'usage du SLX est inconnu : on essaie d'abord l'usage 1 (celui de l'ULX)
            foreach (var c in colls)
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var st = new Status();
                if (!Query(d, m, st)) continue;
                var s = new DeviceStatus { Key = "finalmouse:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Charging = st.Charging == 1 };
                if (st.Soc >= 0) s.Percent = st.Soc;
                else if (st.Mv > 0) s.Percent = PercentFromMillivolts(st.Mv);
                else if (st.Link == 0) s.Connected = false; // récepteur présent, souris endormie ou éteinte
                else continue;
                ctx.Add(s, Vid, m.Pid);
                break;
            }
        }
    }
}
