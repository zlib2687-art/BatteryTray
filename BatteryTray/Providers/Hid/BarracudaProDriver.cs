// Razer Barracuda Pro sur son récepteur 2,4 GHz (1532:053A) : famille de protocole « PA » de Razer, disposition relevée sur une capture USB
// de Razer Synapse rafraîchissant la page batterie.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/barracuda_pro.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT ; testé sur un vrai Barracuda Pro).
//   requête : [01][80][08]['P']['A'][08][08][03][cmd]   (64 octets, rapport 01)
//   mode distant : [01][80][07]['P']['A'][0E][08][02][E1][1 = actif / 0 = inactif] — activé le temps d'une requête puis désactivé,
//                  comme le fait Synapse ; rien n'est mémorisé dans le casque (seule écriture de ce pilote)
//   réponse : [01][80][long]['P']['I'][08][..] ; [13] = écho de la commande, [14] = 01/02, [15] = longueur, [16..] = données
//   cmd 21 = pourcentage, cmd 2A = en charge.
namespace BatteryTray.Providers.Hid;

public static class BarracudaProDriver
{
    const ushort Vid = 0x1532, Pid = 0x053A;

    public static byte[] BuildQuery(byte cmd) => new byte[] { 0x01, 0x80, 0x08, 0x50, 0x41, 0x08, 0x08, 0x03, cmd };
    public static byte[] BuildRemote(bool on) => new byte[] { 0x01, 0x80, 0x07, 0x50, 0x41, 0x0E, 0x08, 0x02, 0xE1, (byte)(on ? 1 : 0) };

    /// <summary>Premier octet de données de la réponse à <paramref name="cmd"/>, ou null si ce n'en est pas une.</summary>
    public static int? ParseReply(byte[] r, byte cmd)
    {
        if (r.Length < 17) return null;
        return r[13] == cmd && (r[14] == 0x01 || r[14] == 0x02) && r[15] > 0 && r[15] <= 32 ? r[16] : null;
    }

    static void Drain(HidDev d) { for (int i = 0; i < 8 && d.Read(20) != null; i++) { } }

    static int Ask(HidDev d, byte cmd)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Drain(d);
            if (!d.Write(BuildQuery(cmd))) return -1;
            for (int i = 0; i < 6; i++)
            {
                var r = d.Read(150);
                if (r != null && ParseReply(r, cmd) is { } v) return v;
            }
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        var colls = ctx.Find(Vid, Pid, c => c.Page == 0xFF00 && c.OutLen >= 64);
        if (colls.Count == 0) return;
        using var d = new HidDev();
        if (!d.Open(colls[0])) return;
        var s = new DeviceStatus { Key = "barracuda:053A", Name = "Razer Barracuda Pro", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
        bool remote = false;
        for (int attempt = 0; attempt < 3 && !remote; attempt++)
        {
            Drain(d);
            remote = d.Write(BuildRemote(true));
            if (!remote) Thread.Sleep(150 * (attempt + 1));
        }
        int level = -1, charging = -1;
        if (remote)
        {
            Thread.Sleep(50);
            level = Ask(d, 0x21);
            if (level >= 0) charging = Ask(d, 0x2A);
            Drain(d);
            d.Write(BuildRemote(false));
        }
        if (level < 0 || level > 100) s.Connected = false; // récepteur présent, casque éteint ou hors de portée
        else { s.Percent = level; s.Charging = charging > 0; }
        ctx.Add(s, Vid, Pid);
    }
}
