// AKG N9 Hybrid sur son récepteur USB-C (0ECB:20D9, identifiant Harman).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/akg.cpp, porté en C# — voir NOTICE.md.
// Le récepteur est une puce Airoha et parle le protocole RACE d'Airoha sur la collection vendeur FF13:0001 (interface 3), encapsulé comme
// pour l'Audeze Maxwell :
//   requête : rapport de sortie 06 de 62 octets : [06][07][80][05 5A 03 00 D6 0C 00] = commande RACE (05 5A), longueur 3, id 0x0CD6 (batterie), agent 00
//   réponse : rapport d'entrée 07 (GetInputReport) : [07][longueur valide][80] puis des trames RACE récentes ; la réponse batterie est
//             [05 5D 05 00 D6 0C][état 00][agent][pourcentage]. Casque éteint : aucune réponse batterie dans la partie valide.
// Vérifié sur un N9 Hybrid : 05 5D 05 00 D6 0C 00 00 32 = 50 %, comme l'application AKG. Une seule requête de lecture ; pas d'indicateur de charge connu.
namespace BatteryTray.Providers.Hid;

public static class AkgDriver
{
    const ushort Vid = 0x0ECB, N9HybridDongle = 0x20D9;

    public static byte[] BuildRequest() => new byte[] { 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00 };

    /// <summary>
    /// Dernière réponse batterie d'un tampon du rapport 07, ou -1. L'octet 1 donne le nombre d'octets valides à partir de l'octet 3 ; le reste
    /// date d'anciennes réponses : casque éteint, une vieille trame de batterie peut y traîner et ne doit pas être lue.
    /// </summary>
    public static int FindLevel(byte[] b)
    {
        if (b.Length < 4) return -1;
        int end = Math.Min(b.Length, 3 + b[1]);
        int level = -1;
        for (int i = 3; i + 8 < end; i++)
        {
            if (b[i] != 0x05 || b[i + 1] != 0x5D || b[i + 2] != 0x05 || b[i + 3] != 0x00) continue;
            if (b[i + 4] != 0xD6 || b[i + 5] != 0x0C || b[i + 6] != 0x00) continue; // id 0x0CD6, état ok
            if (b[i + 8] <= 100) level = b[i + 8];
        }
        return level;
    }

    static int ReadLevel(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c) || !d.Write(BuildRequest())) return -1;
        long end = Environment.TickCount64 + 600; // la réponse arrive en ~100 ms casque allumé
        while (Environment.TickCount64 < end)
        {
            Thread.Sleep(15);
            if (!d.GetInputReport(0x07, out var r)) continue;
            int l = FindLevel(r);
            if (l >= 0) return l;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, N9HybridDongle)) return;
        foreach (var c in ctx.Find(Vid, N9HybridDongle, c => c.Iface == 3 && c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen == 62 && c.OutLen == 62))
        {
            int level = ReadLevel(c);
            if (level < 0) continue;
            ctx.Add(new DeviceStatus { Key = "akg:" + HidUtil.Hex4(N9HybridDongle), Name = "AKG N9 Hybrid", Kind = DeviceKind.Headset, Via = "USB dongle", Percent = level }, Vid, N9HybridDongle);
            break;
        }
    }
}
