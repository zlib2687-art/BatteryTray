// AULA X9 Ultra, vérifié sur le récepteur 320F:22A0, FF1C:0092, rapports de 64 octets. Cet identifiant de récepteur est partagé avec les souris V9 : on ne déduit pas le modèle de l'identifiant.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/aula_mouse.cpp et aula_mouse_protocol.h, porté en C# — voir NOTICE.md.
// Références : HeyOkay/HaloBattery issue #325 et ahmedkhursheed23/HaloBattery (providers/lofree.py). Vérifié indépendamment : DÉBUT 01, batterie 1A, FIN 02, identifiant de rapport 04.
// Réponse batterie : octet 8 = %, octet 9 = 0 batterie / 1 en charge / 2 plein. Le récepteur met le niveau en cache. Une trame AA « liaison coupée » poussée marque la souris endormie ; l'absence de battement n'est PAS une preuve
// de déconnexion. Zéro est un cache non initialisé. Seules des transactions de lecture d'état ; aucun réglage, appairage ni micrologiciel.
namespace BatteryTray.Providers.Hid;

public static class AulaMouseDriver
{
    const ushort Vid = 0x320F, Pid = 0x22A0;

    public static byte[] Request(byte cmd) { var r = new byte[64]; r[0] = 4; r[3] = cmd; return r; }

    public static bool IsHeartbeat(byte[] r) => r.Length >= 9 && r[0] == 4 && r[1] == 0xAA && r[2] == 0 && r[3] == 0xAA && (r[8] == 0 || r[8] == 0xFF);

    public static bool IsReply(byte[] r, byte cmd) => r.Length >= 8 && r[0] == 4 && r[1] == 0 && r[2] == 0 && r[3] == cmd && r[4] == 0 && r[5] == 0 && r[6] == 0 && r[7] == 0;

    public static (int Percent, bool Charging)? ParseBattery(byte[] r) =>
        r.Length < 10 || !IsReply(r, 0x1A) || r[8] == 0 || r[8] > 100 || r[9] > 2 ? null : (r[8], r[9] != 0);

    static byte[]? WaitReply(HidDev d, byte cmd, long end, ref bool linkDown)
    {
        while (Environment.TickCount64 < end)
        {
            long left = end - Environment.TickCount64;
            if (left <= 0) break;
            var r = d.Read(left > 100 ? 100 : (int)left);
            if (r == null) continue;
            if (IsHeartbeat(r)) { linkDown = r[8] == 0; continue; }
            if (IsReply(r, cmd) && (cmd != 0x1A || r.Length >= 10)) return r;
        }
        return null;
    }

    static (int Percent, bool Charging)? ReadBattery(HidColl c, ref bool linkDown)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        // Toute la transaction, nettoyage compris, est bornée à une seconde.
        long end = Environment.TickCount64 + 1000;
        if (!d.Write(Request(1), 150)) return null;
        bool started = WaitReply(d, 1, end - 550, ref linkDown) != null;
        (int, bool)? valid = null;
        if (started && d.Write(Request(0x1A), 150) && WaitReply(d, 0x1A, end - 200, ref linkDown) is { } reply) valid = ParseBattery(reply);
        // On termine même sans accusé de réception, pour ne laisser aucune transaction ouverte.
        if (d.Write(Request(2), 100)) WaitReply(d, 2, end, ref linkDown);
        return valid;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Iface == 1 && c.Page == 0xFF1C && c.Usage == 0x0092 && c.InLen == 64 && c.OutLen == 64))
        {
            if (c.Product != "AULA-8K Dongle") continue;
            var s = new DeviceStatus { Key = "aula_mouse:320F:22A0", Name = "AULA wireless mouse (X9 / V9)", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
            bool linkDown = false;
            if (ReadBattery(c, ref linkDown) is { } p && !linkDown) { s.Percent = p.Percent; s.Charging = p.Charging; }
            else s.Connected = false;
            ctx.Add(s, Vid, Pid);
            return;
        }
    }
}
