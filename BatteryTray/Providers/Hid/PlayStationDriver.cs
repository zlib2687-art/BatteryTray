// Sony DualShock 4 / DualSense / DualSense Edge (VID 054C), en USB ou en Bluetooth.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/playstation.cpp, porté en C# — voir NOTICE.md.
// La batterie fait partie du rapport d'entrée habituel :
//   DualShock 4 : rapport USB 01 octet 30, Bluetooth 11 octet 32 : bits 0-3 niveau, bit 4 câble
//   DualSense   : rapport USB 01 octet 53, Bluetooth 31 octet 54 : bits 0-3 niveau (0-10), bits 4-7 état
// En Bluetooth, les deux manettes envoient un rapport réduit tant qu'un rapport de fonctionnalité n'est pas lu : on lit une fois
// le rapport de calibration (05), comme le font les jeux. Disposition : pilote Linux drivers/hid/hid-playstation.c. Lecture seule.
namespace BatteryTray.Providers.Hid;

public static class PlayStationDriver
{
    public static bool IsDs4(ushort pid) => pid is 0x05C4 or 0x09CC or 0x0BA0;
    public static bool IsDualSense(ushort pid) => pid is 0x0CE6 or 0x0DF2;

    public static string Name(ushort pid) => IsDs4(pid) ? "DualShock 4" : pid == 0x0DF2 ? "DualSense Edge" : "DualSense";

    public enum Outcome { NotThisReport, Found, AdapterWithoutController }

    /// <summary>
    /// Lit un rapport d'entrée : (résultat, pourcentage, en charge). « NotThisReport » : rapport d'un autre type ou trop court (on continue à lire) ;
    /// « AdapterWithoutController » : adaptateur sans fil sans manette (octet de batterie à 0).
    /// </summary>
    public static (Outcome Result, int Percent, bool Charging) Parse(ushort pid, byte[] r, bool bluetooth)
    {
        if (r.Length < 10) return (Outcome.NotThisReport, -1, false);
        if (IsDs4(pid))
        {
            int at = r[0] == 0x01 ? 30 : r[0] == 0x11 ? 32 : 0;
            if (at == 0 || r.Length <= at || (r[0] == 0x01 && bluetooth)) return (Outcome.NotThisReport, -1, false);
            int level = r[at] & 0x0F;
            bool cable = (r[at] & 0x10) != 0;
            if (pid == 0x0BA0 && r[at] == 0) return (Outcome.AdapterWithoutController, -1, false);
            int pct = cable ? (level >= 11 ? 100 : HidUtil.Clamp100(level * 10)) : HidUtil.Clamp100(level * 10 + 5);
            return (Outcome.Found, pct, cable && level < 11);
        }
        else
        {
            int at = r[0] == 0x01 ? 53 : r[0] == 0x31 ? 54 : 0;
            if (at == 0 || r.Length <= at || (r[0] == 0x01 && bluetooth)) return (Outcome.NotThisReport, -1, false);
            int level = r[at] & 0x0F, state = r[at] >> 4;
            int pct = HidUtil.Clamp100(level * 10 + 5);
            bool charging = state == 0x1;
            if (state == 0x2) { pct = 100; charging = true; }
            return (Outcome.Found, pct, charging);
        }
    }

    /// <summary>Hachage FNV-1a du chemin du périphérique : distingue deux manettes identiques (le chemin change avec le port).</summary>
    public static string PathHash(string path)
    {
        uint h = 2166136261u;
        foreach (char ch in path) { h ^= ch; h *= 16777619u; }
        return h.ToString();
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var c in ctx.Colls.ToList())
        {
            if (c.Vid != 0x054C || !(IsDs4(c.Pid) || IsDualSense(c.Pid))) continue;
            if (c.Page != 0x01 || c.Usage != 0x05) continue; // la collection « manette »
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            bool bt = c.InLen > 64;
            if (bt) d.GetFeature(0x05, out _); // passe la manette en rapports complets

            int pct = -1; bool charging = false, found = false;
            long end = Environment.TickCount64 + 500;
            while (!found && end - Environment.TickCount64 > 0)
            {
                var r = d.Read(100);
                if (r == null) continue;
                var p = Parse(c.Pid, r, bt);
                if (p.Result == Outcome.AdapterWithoutController) break;
                if (p.Result == Outcome.Found) { pct = p.Percent; charging = p.Charging; found = true; }
            }
            if (!found) continue;
            ctx.Out.Add(new DeviceStatus
            {
                Key = $"ps:{HidUtil.Hex4(c.Pid)}:{PathHash(c.Path)}",
                Name = Name(c.Pid), Kind = DeviceKind.Gamepad,
                Via = bt ? "Bluetooth" : (c.Pid == 0x0BA0 ? "Wireless adapter" : "USB cable"),
                Percent = pct, Charging = charging,
            });
            ctx.Claim(c.Vid, c.Pid);
        }
    }
}
