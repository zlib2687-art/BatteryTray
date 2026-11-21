// Claviers SONiX sur leur récepteur 2,4 GHz : AJAZZ AK820 MAX (« AJAZZ AK820 MAX 2.4G », 05AC:024F, testé), AULA F108 Pro (même récepteur 05AC:024F), AJAZZ AK980 V2 (0C45:FEFE) et AJAZZ AK820 Pro (0C45:FDFD, certains
// exemplaires livrent un « 2.4G Dongle » 0C45:FEFE). Le récepteur 05AC réutilise l'identifiant d'Apple : les récepteurs sont donc aussi reconnus par leur collection vendeur : page FF60 / usage 61, rapports d'entrée et de
// sortie de 33 octets (identifiant 0 + 32 octets). Ces récepteurs exigent l'interface 3 ; EPOMAKER P75 (342D:E479, demande #2525, non testé sur matériel) utilise l'interface 1.
//   requête [20 01 00… somme] (somme = somme des 31 octets précédents) ; réponse [20 01 00 niveau…], niveau en %.
// Une réponse se reconnaît à ses trois premiers octets (la commande en écho), comme le font les outils AK820 Pro ; sa somme n'est pas vérifiée. Le clavier ne signale son niveau qu'en mode 2,4G. Protocole en lecture seule.
// Les récepteurs MCHOSE K87 du même identifiant 05AC:024F ont une autre disposition (collection BY sur l'interface 1, pas de FF60 sur l'interface 3) : AulaDriver passe avant et les lit.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/sonix.cpp, porté en C# — voir NOTICE.md. Source : donglover69/sonix-keyboard-studio (README, « 2.4G dongles ») ; AK820 Pro 0C45:FDFD et correspondance sur l'écho :
// deddroid/ak820-rs (device.rs, protocol.rs) et usharik/ak820-studio (Keyboard.swift).
namespace BatteryTray.Providers.Hid;

public static class SonixDriver
{
    /// <param name="Iface">&lt; 0 : on reconnaît la collection vendeur exacte sur n'importe quelle interface.</param>
    public sealed record Dongle(ushort Vid, ushort Pid, int Iface);

    public static readonly Dongle[] Dongles =
    {
        new(0x05AC, 0x024F, 3), new(0x0C45, 0xFEFE, 3), new(0x0C45, 0xFDFD, 3),
        new(0x342D, 0xE479, -1), new(0x342D, 0xE485, -1), new(0x342D, 0xE4BC, -1), new(0x342D, 0xE4C2, -1), new(0x342D, 0xE407, -1), // matériel non testé pour tous ceux-ci
        new(0x388D, 0x0003, -1), new(0x0DB0, 0x0CA0, -1), new(0x36B0, 0x30BD, -1), new(0x342D, 0xE51A, -1), new(0x342D, 0xE487, -1), new(0x342D, 0xE537, -1),
        new(0x388D, 0x0002, -1), new(0x19F5, 0x3245, -1), new(0x362D, 0xD028, -1), new(0x362D, 0xD030, -1), new(0x362D, 0xD101, -1), new(0x342D, 0xE49F, -1),
    };

    const int Len = 32; // octets de charge après l'identifiant de rapport ; le dernier est la somme

    public static byte[] BuildRequest()
    {
        var req = new byte[1 + Len]; // [0] = identifiant de rapport 0
        req[1] = 0x20; req[2] = 0x01;
        int sum = 0; for (int i = 1; i < Len; i++) sum += req[i];
        req[Len] = (byte)sum;
        return req;
    }

    /// <summary>Réponse lue (identifiant 0 retiré) : niveau (octet 3) si l'écho [20 01 00] est correct, sinon null.</summary>
    public static int? ParseReply(byte[] r) => r.Length < 4 || r[0] != 0x20 || r[1] != 0x01 || r[2] != 0x00 ? null : r[3];

    public static string KeyboardName(string product)
    {
        const string suffix = " 2.4G";
        if (product.Length > suffix.Length && product.EndsWith(suffix, StringComparison.Ordinal)) product = product[..^suffix.Length];
        return product.Length == 0 ? "Wireless keyboard" : product;
    }

    public static bool IsVendorCollection(Dongle m, HidColl c) =>
        (m.Iface < 0 || c.Iface == m.Iface) && c.Page == 0xFF60 && c.Usage == 0x0061 && c.OutLen == 1 + Len && c.InLen == 1 + Len;

    static int QueryLevel(HidDev d)
    {
        d.Flush();
        if (!d.Write(BuildRequest())) return -1;
        long end = Environment.TickCount64 + 500;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return -1;
            var r = d.Read(left);
            if (r == null) return -1;
            if (ParseReply(r) is { } v) return v;
        }
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Dongles)
        {
            if (ctx.IsClaimed(m.Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(m.Vid, m.Pid, c => IsVendorCollection(m, c)))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                int level = QueryLevel(d);
                if (level < 0) level = QueryLevel(d); // la première requête peut rester sans réponse pendant le réveil de la liaison 2,4 GHz
                if (level < 0) continue;
                var s = new DeviceStatus { Key = "sonix:" + HidUtil.Hex4(m.Vid) + ":" + HidUtil.Hex4(m.Pid), Name = KeyboardName(c.Product), Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Connected = level is >= 1 and <= 100 }; // 0 : clavier éteint, hors de portée ou dans un autre mode
                if (s.Connected) s.Percent = level;
                ctx.Add(s, m.Vid, m.Pid);
                break;
            }
        }
    }
}
