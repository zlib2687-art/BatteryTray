// Casque ENDORFY Viro Wireless sur son récepteur 2,4 GHz (3738:0072).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/endorfy.cpp, porté en C# — voir NOTICE.md.
// Aucune source ENDORFY ne documente ce récepteur. Sa disposition HID est celle d'un récepteur USB Airoha standard : une seule collection
// vendeur FF13:0001 (interface 3), rapports de 62 octets (06 en sortie, 07 en entrée). Elle est lue avec la requête « batterie » RACE générique d'Airoha
// (HaloBattery, MIT : providers/skullcandy.py et audeze.py ; projet Headroom, SDK Airoha) :
//   requête (rapport 06, 62 octets) : 06 07 80 05 5A 03 00 D6 0C 00
//   réponse (rapport 07, GetInputReport) : [07][longueur][destinataire] puis des trames RACE 05 <type> <u16 long.> <u16 code> <charge> ;
//   la batterie est l'indication 0x5D du code 0x0CD6, charge [état 0][rôle][pourcentage] (elle peut suivre l'accusé 0x5B). Pas d'indicateur de charge.
// Casque éteint ou récepteur muet : rien n'est affiché et le récepteur est « pris en charge » pour qu'aucun autre pilote ne lui écrive.
namespace BatteryTray.Providers.Hid;

public static class EndorfyDriver
{
    const ushort Vid = 0x3738, Dongle = 0x0072;
    const int ReportLen = 62;

    public static byte[] BuildRequest() => new byte[] { 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00 };

    public static bool IsVendorCollection(HidColl c) => c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen == ReportLen && c.OutLen == ReportLen;

    /// <summary>Pourcentage porté par un rapport d'entrée 07, ou -1 s'il ne contient aucune indication de batterie.</summary>
    public static int BatteryPercent(byte[] r)
    {
        if (r.Length < 9 || r[0] != 0x07) return -1;
        int i = 3;
        while (i + 6 <= r.Length)
        {
            if (r[i] != 0x05) { i++; continue; }
            int len = r[i + 2] | (r[i + 3] << 8);
            if (len < 2 || i + 4 + len > r.Length) break;
            int op = r[i + 4] | (r[i + 5] << 8);
            if (r[i + 1] == 0x5D && op == 0x0CD6 && len >= 5 && r[i + 6] == 0 && r[i + 8] <= 100) return r[i + 8];
            i += 4 + len;
        }
        return -1;
    }

    static int ReadLevel(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return -1;
        // Le rapport 07 garde la réponse précédente : une lecture identique n'est pas prise pour la nouvelle (sauf la dernière).
        byte[]? before = d.GetInputReport(0x07, out var b0) ? b0 : null;
        if (!d.Write(BuildRequest())) return -1;
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(60);
            if (!d.GetInputReport(0x07, out var r)) continue;
            if (before != null && r.AsSpan().SequenceEqual(before) && i + 1 < 12) continue;
            int level = BatteryPercent(r);
            if (level >= 0) return level;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Dongle)) return;
        foreach (var c in ctx.Find(Vid, Dongle, IsVendorCollection))
        {
            int level = ReadLevel(c);
            ctx.Claim(Vid, Dongle);
            if (level < 0) return; // casque éteint ou pas de réponse
            ctx.Add(new DeviceStatus { Key = "endorfy:viro", Name = "ENDORFY Viro Wireless", Kind = DeviceKind.Headset, Via = "2.4 GHz dongle", Percent = level }, Vid, Dongle);
            return;
        }
    }
}
