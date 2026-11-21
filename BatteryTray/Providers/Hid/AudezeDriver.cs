// Casques Audeze Maxwell (VID 3329) sur le récepteur USB ou le câble USB-C.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/audeze.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT) dont les paquets viennent de HeadsetControl (audeze_maxwell.hpp).
//   Collection vendeur FF13:0001. Requête : rapport de sortie 06 de 62 octets ; réponse : rapport d'entrée 07 de 62 octets lu par GetInputReport.
//   La réponse est un tampon tournant de réponses récentes : on y cherche le marqueur de batterie D6 0C 00 00 <pourcentage>.
// Seule la requête de batterie (attribut 0x0CD6) est envoyée ; la séquence d'initialisation de HeadsetControl, dont un paquet n'est pas
// confirmé en lecture seule, est volontairement omise. Sans casque lié, le récepteur se nomme « … Dongle » et répond une valeur périmée :
// on ne l'interroge pas alors. Pas d'indicateur de charge : un Maxwell charge toujours sur son câble USB-C, donc câble = en charge.
namespace BatteryTray.Providers.Hid;

public static class AudezeDriver
{
    const ushort Vid = 0x3329;

    public sealed record Model(ushort Pid, bool Cable, string Name);
    // Câbles d'abord : la connexion directe l'emporte.
    public static readonly Model[] Models =
    {
        new(0x4B1A, true, "Audeze Maxwell"), new(0x4B1E, true, "Audeze Maxwell"),
        new(0x4B19, false, "Audeze Maxwell"), new(0x4B18, false, "Audeze Maxwell"),
        new(0x4B29, false, "Audeze Maxwell 2"), new(0x4B28, false, "Audeze Maxwell 2"),
    };

    public static byte[] BuildRequest() => new byte[] { 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C };

    /// <summary>Cherche le marqueur de batterie dans un tampon de réponses ; -1 si absent.</summary>
    public static int FindLevel(byte[] b)
    {
        for (int i = 0; i + 4 < b.Length; i++)
            if (b[i] == 0xD6 && b[i + 1] == 0x0C && b[i + 2] == 0x00 && b[i + 3] == 0x00 && b[i + 4] <= 100) return b[i + 4];
        return -1;
    }

    /// <summary>Un récepteur dont le nom se termine par « dongle » n'a pas de casque lié.</summary>
    public static bool IsUnlinkedDongle(Model m, string product) => !m.Cable && product.TrimEnd().EndsWith("dongle", StringComparison.OrdinalIgnoreCase);

    static int ReadLevel(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return -1;
        int level = -1;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!d.Write(BuildRequest())) return -1;
            for (int i = 0; i < 4; i++) // la réponse peut arriver un peu plus tard ; la trame la plus récente compte
            {
                Thread.Sleep(60);
                if (!d.GetInputReport(0x07, out var r)) continue;
                int l = FindLevel(r);
                if (l >= 0) level = l;
                if (i >= 2 && level >= 0) return level;
            }
            if (level >= 0) return level;
        }
        return level;
    }

    // Un casque affiche 0 % un instant juste après sa mise sous tension.
    static readonly Dictionary<ushort, long> FirstSeen = new();

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen > 0);
            if (colls.Count == 0) { FirstSeen.Remove(m.Pid); continue; }
            if (IsUnlinkedDongle(m, colls[0].Product)) { ctx.Claim(Vid, m.Pid); FirstSeen.Remove(m.Pid); continue; }
            int level = ReadLevel(colls[0]);
            long now = Environment.TickCount64;
            if (!FirstSeen.ContainsKey(m.Pid)) FirstSeen[m.Pid] = now;
            var s = new DeviceStatus
            {
                Key = "audeze:maxwell", Name = m.Name, Kind = DeviceKind.Headset, // récepteur et câble = un seul casque
                Via = m.Cable ? "USB cable" : "2.4 GHz dongle",
            };
            if (level < 0 || (level == 0 && now - FirstSeen[m.Pid] < 90_000)) s.Connected = false; // pas encore de lecture, ou 0 % de la mise sous tension
            else { s.Percent = level; s.Charging = m.Cable; }
            ctx.Add(s, Vid, m.Pid);
            foreach (var o in Models) ctx.Claim(Vid, o.Pid);
            return;
        }
    }
}
