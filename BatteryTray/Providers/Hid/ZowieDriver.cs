// Souris sans fil BenQ ZOWIE (VID 04A5). Zowie ne fournit aucun logiciel : ces commandes vendeur ont été déduites par la communauté.
// Seules les commandes de batterie en lecture seule ci-dessous sont envoyées (d'autres commandes EC-CW redémarrent le récepteur et désappairent la souris).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/zowie.cpp, porté en C# — voir NOTICE.md.
//  * Série EC-CW (EC1 / EC2 / EC3-CW) : récepteur 8006, câble 8005. Rapport de sortie non numéroté de 64 octets [A5 03] ; réponse
//    [mV lo][mV hi][pourcentage]… ; mV = 0 si la souris n'est pas liée. Le « Enhanced Receiver » déclare cette collection sur la page 00FF au lieu de FFxx.
//    Source : github.com/yleshchuk/zowie-battery.
//  * Série DW (U2-DW, S2-DW…) : récepteur 800A. Rapport de sortie 08 [B4 15] sur la page FF03, réponse = rapport d'entrée 09 sur la page FF04 :
//    mV = ([1] × 256 + [2]) × 10, [4] == C8. Source : github.com/IgnacioBarb/zowie-battery-tray (MIT).
//  * U2 : récepteur 8008 (« BenQ ZOWIE Gaming Mouse »), mêmes collections et commandes que la série DW.
// Sur la base de charge la souris charge par contacts : rien de nouveau côté USB, et par radio seule la tension arrive. La charge est donc déduite de
// l'évolution de la tension (règles de zowie-battery-tray) : une hausse de 50 mV au-dessus du minimum = posée sur la base, une baisse de 20 mV sous
// le maximum = retirée. Le récepteur ne transmet une tension que lorsque la souris en envoie une : les valeurs changent par paliers.
namespace BatteryTray.Providers.Hid;

public static class ZowieDriver
{
    const ushort Vid = 0x04A5;

    static readonly int[] V = { 4200, 4000, 3890, 3760, 3680, 3500 };
    static readonly int[] P = { 100, 75, 50, 25, 10, 0 };

    /// <summary>Tension → pourcentage, ancré sur les seuils des LED de la souris (10 / 25 / 50 %).</summary>
    public static int Percent(int mv) => HidUtil.Interpolate(mv, V, P);

    /// <summary>Estimation de la charge d'après l'évolution de la tension d'un récepteur.</summary>
    public sealed class ChargeTrend
    {
        bool _has, _charging; int _peak, _trough;
        public bool Update(int mv)
        {
            if (!_has) { _has = true; _charging = mv >= 4250; _peak = _trough = mv; } // une cellule Li-ion au repos ne dépasse jamais ~4,2 V
            else if (_charging) { _peak = Math.Max(_peak, mv); if (mv <= _peak - 20) { _charging = false; _trough = mv; } }
            else { _trough = Math.Min(_trough, mv); if (mv >= _trough + 50) { _charging = true; _peak = mv; } }
            return _charging;
        }
    }

    /// <summary>Réponse EC-CW : (millivolts, pourcentage annoncé) ; null si ce n'est pas une réponse de batterie.</summary>
    public static (int Millivolts, int Percent)? ParseCw(byte[] r)
    {
        if (r.Length < 3) return null;
        int mv = r[0] | (r[1] << 8);
        if (mv != 0 && (mv < 3000 || mv > 4500)) return null;
        return (mv, r[2] is >= 1 and <= 100 ? r[2] : -1);
    }

    /// <summary>Réponse DW / U2 (rapport 09) : millivolts, ou null si invalide (E6 = erreur, [4] doit valoir C8).</summary>
    public static int? ParseDw(byte[] r) => r.Length < 5 || r[0] != 0x09 || r[1] == 0xE6 || r[4] != 0xC8 ? null : (r[1] * 256 + r[2]) * 10;

    static readonly Dictionary<ushort, ChargeTrend> Trends = new();

    static bool PollCw(PollContext ctx, ushort pid)
    {
        foreach (var c in ctx.Find(Vid, pid, c => (c.Page >= 0xFF00 || c.Page == 0x00FF) && c.OutLen == 65))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(0x00, 0xA5, 0x03)) continue;
            var r = d.Read(1000);
            if (r == null || ParseCw(r) is not { } cw) continue;
            var s = new DeviceStatus { Key = "zowie:" + HidUtil.Hex4(pid), Name = "ZOWIE EC-CW", Kind = DeviceKind.Mouse, Via = pid == 0x8005 ? "USB cable" : "2.4 GHz receiver", Charging = pid == 0x8005 };
            if (cw.Millivolts == 0) s.Connected = false;
            else s.Percent = cw.Percent >= 0 ? cw.Percent : Percent(cw.Millivolts);
            ctx.Add(s, Vid, pid);
            return true;
        }
        return false;
    }

    static bool PollDw(PollContext ctx, ushort pid)
    {
        var cmd = ctx.Find(Vid, pid, c => c.Page == 0xFF03 && c.OutLen == 16);
        var rep = ctx.Find(Vid, pid, c => c.Page == 0xFF04 && c.InLen == 16);
        if (cmd.Count == 0 || rep.Count == 0) return false;
        using var din = new HidDev(); using var dout = new HidDev();
        if (!din.Open(rep[0]) || !dout.Open(cmd[0])) return false; // le canal de réponse d'abord
        din.Flush();
        if (!dout.Write(0x08, 0xB4, 0x15)) return false;
        int? mv = null;
        long end = Environment.TickCount64 + 1500;
        while (end - Environment.TickCount64 > 0)
        {
            var r = din.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 5) break;
            if (r[0] == 0x09) { mv = ParseDw(r); break; }
        }
        if (mv == null) return false;
        bool cable = pid != 0x800A && pid != 0x8008; // 800A : récepteur DW, 8008 : récepteur U2
        string name = cmd[0].Product;
        if (pid == 0x8008) name = "ZOWIE U2"; // son récepteur ne se déclare que « BenQ ZOWIE Gaming Mouse »
        else if (name.Length == 0 || name.Contains("Receiver", StringComparison.Ordinal)) name = "ZOWIE DW mouse";
        var s = new DeviceStatus { Key = "zowie:" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver" };
        if (mv == 0) s.Connected = false;
        else
        {
            s.Percent = Percent(mv.Value);
            if (!Trends.TryGetValue(pid, out var tr)) Trends[pid] = tr = new ChargeTrend();
            s.Charging = cable || tr.Update(mv.Value);
        }
        ctx.Add(s, Vid, pid);
        return true;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (_, pid) in ctx.Products(Vid))
        {
            if (pid is 0x8005 or 0x8006) PollCw(ctx, pid);
            else PollDw(ctx, pid); // n'agit que sur les appareils ayant les collections commande / réponse DW
        }
    }
}
