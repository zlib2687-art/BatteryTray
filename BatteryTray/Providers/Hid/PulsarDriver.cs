// Pulsar X2 V3 / X2 V3 eS et autres souris Pulsar à puce Sonix, identifiant fabricant 3710 :
//   X2 V3 : câble USB 3402 (« X2 v3 »), récepteur 1K 5402 (confirmé par un --dump d'utilisateur, issue #54) ; X2 V3 eS : câble 3406, récepteur 8K 5403.
// Les deux récepteurs sont partagés avec d'autres Pulsar (Xlite V4, X3) : sur un récepteur la souris reçoit donc un nom générique. Ces souris ne parlent PAS le protocole à rapport 08 de 17 octets du pilote Kysona.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/pulsar.cpp, porté en C# — voir NOTICE.md.
// Protocole : darthsoup/PulsarBattery (Sonix64Protocol.cs / Sonix64Backend.cs) et bluesceada/pulsar-mouse-linux (drivers/xlite_v4_wireless.py, vérifié sur 5402 avec un X3) :
//   rapport de fonctionnalité 0, paquet de 64 octets : [00][catégorie][registre][sous][00…][somme lo][somme hi], somme = somme 16 bits des octets 0..61 du paquet (petit-boutiste en 62..63).
//   Lecture batterie = 08 81 01. SET_FEATURE puis GET_FEATURE toutes les ~50 ms jusqu'à l'écho : [état][08][81][01][..][..][%] (octet 6, FF = pas de lecture).
//   État 01 = répondu, 02 = en attente. 05 = « souris non liée / endormie » pour pulsar-mouse-linux mais « en attente » pour PulsarBattery : on continue donc sur 05 et on ne le traite comme « souris éteinte » que
//   s'il est encore la dernière réponse à l'abandon. La collection vendeur (FFFF:0001) est sur les interfaces 2 et 3 ; l'interface 2 refuse SET_FEATURE : les interfaces hautes d'abord.
//   Pas de registre de charge fiable : sur câble la souris charge ; 0 % d'un récepteur = souris éteinte ou hors de portée.
namespace BatteryTray.Providers.Hid;

public static class PulsarDriver
{
    const ushort Vid = 0x3710;

    public sealed record Product(ushort Pid, bool Cable, string? Name);
    public static readonly Product[] Products = { new(0x3402, true, "Pulsar X2 V3"), new(0x3406, true, "Pulsar X2 V3 eS"), new(0x5402, false, null), new(0x5403, false, null) };

    public enum Kind { Battery, Offline, None }

    public static byte[] BuildRequest()
    {
        var req = new byte[65]; // [0] = identifiant de rapport 0, puis le paquet de 64 octets
        req[2] = 0x08; req[3] = 0x81; req[4] = 0x01;
        int sum = 0; for (int i = 1; i <= 62; i++) sum += req[i];
        req[63] = (byte)(sum & 0xFF); req[64] = (byte)(sum >> 8);
        return req;
    }

    /// <summary>Réponse GET_FEATURE (identifiant 0 compris) : null = pas encore notre réponse ; Battery avec % ; Pending (02 / 05) ; None si FF.</summary>
    public enum Step { NotOurs, Battery, Pending, PendingOffline, NoReading }

    public static (Step Result, int Percent) ParseReply(byte[] r)
    {
        if (r.Length < 65) return (Step.NoReading, -1);
        var w = r.AsSpan(1);
        if (w[1] != 0x08 || w[2] != 0x81 || w[3] != 0x01) return (Step.NotOurs, -1);
        if (w[0] == 0x01) return w[6] > 100 ? (Step.NoReading, -1) : (Step.Battery, w[6]);
        return (w[0] == 0x05 ? Step.PendingOffline : Step.Pending, -1);
    }

    static (Kind, int) ReadBattery(HidDev d)
    {
        var req = BuildRequest(); bool sawOffline = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!d.SetFeature(req)) return (Kind.None, -1);
            for (int read = 0; read < 6; read++)
            {
                Thread.Sleep(50);
                if (!d.GetFeature(0x00, out var r)) return (Kind.None, -1);
                var (step, pct) = ParseReply(r);
                if (step == Step.NoReading) return (Kind.None, -1);
                if (step == Step.NotOurs) continue;
                if (step == Step.Battery) return (Kind.Battery, pct);
                sawOffline = step == Step.PendingOffline; // 02 (ou 05) = en attente : on continue
            }
        }
        return (sawOffline ? Kind.Offline : Kind.None, -1);
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var prod in Products)
        {
            if (ctx.IsClaimed(Vid, prod.Pid)) continue;
            var colls = ctx.Find(Vid, prod.Pid, c => c.FeatLen >= 65).OrderByDescending(c => c.Iface).ToList();
            foreach (var c in colls)
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                var (rep, pct) = ReadBattery(d);
                if (rep == Kind.None) continue;
                var s = new DeviceStatus { Key = "pulsar:" + HidUtil.Hex4(prod.Pid), Kind = DeviceKind.Mouse };
                if (prod.Cable) { s.Name = prod.Name!; s.Via = "USB cable"; s.Charging = true; }
                else
                {
                    bool generic = c.Product.Length == 0 || c.Product.Contains("Dongle", StringComparison.Ordinal) || c.Product.Contains("Receiver", StringComparison.Ordinal);
                    s.Name = generic ? "Pulsar wireless mouse" : c.Product; // les récepteurs sont partagés par plusieurs modèles
                    s.Via = "2.4 GHz receiver";
                    s.Connected = rep == Kind.Battery && pct > 0;
                }
                if (rep == Kind.Battery && (pct > 0 || prod.Cable)) s.Percent = pct;
                ctx.Add(s, Vid, prod.Pid);
                break;
            }
        }
    }
}
