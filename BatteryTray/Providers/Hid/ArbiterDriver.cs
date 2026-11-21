// Souris sans fil Arbiter Studio Akitsu sur l'identifiant DriverCore partagé 373E :
//   Akitsu (PAW3395) : 0009 câble USB, 000A récepteur 8K ; Akitsu 3950 (PAW3950) : 0038 / 0039 ; Akitsu Medium (PAW3950) : 0066 / 0067 (vérifiés sur matériel).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/arbiter.cpp, porté en C# — voir NOTICE.md.
// À exécuter AVANT LamzuDriver, qui parcourt tous les produits 373E non pris avec la même requête DriverCore et les nommerait « LAMZU wireless mouse ».
// Protocole et identifiants de l'Arbiter Web HUB (webapp.arbiterstudio.com, getBatPer() « IsNewProtocol » du paquet DriverCore ; voir aussi LamzuDriver) :
//   collection vendeur FFFF:0000 (rapport de fonctionnalité 0 de 65 octets) : SET_FEATURE [00][00][00][02][02][00][83][00…], attente ~100 ms, GET_FEATURE rapport 0 →
//   [00][état][..][..][02][..][83][charge][pourcentage] (ou sans l'octet d'identifiant en tête). État A1 / A2 = répondu, A0 = souris endormie ; charge 1 = sur câble ;
//   pourcentage 0 sur un récepteur = souris éteinte ou non liée. Lecture seule : aucun réglage ni changement de mode n'est écrit.
namespace BatteryTray.Providers.Hid;

public static class ArbiterDriver
{
    const ushort Vid = 0x373E;

    // Câbles avant récepteurs : câble et récepteur 8K branchés ensemble, un câble qui répond prend son récepteur pour que la souris ne soit listée qu'une fois.
    public sealed record Model(ushort Pid, bool Receiver, string Name, ushort ReceiverPid);
    public static readonly Model[] Models =
    {
        new(0x0009, false, "Arbiter Studio Akitsu", 0x000A), new(0x0038, false, "Arbiter Studio Akitsu 3950", 0x0039), new(0x0066, false, "Arbiter Studio Akitsu Medium", 0x0067),
        new(0x000A, true, "Arbiter Studio Akitsu", 0), new(0x0039, true, "Arbiter Studio Akitsu 3950", 0), new(0x0067, true, "Arbiter Studio Akitsu Medium", 0),
    };

    public enum Kind { Battery, Asleep, None }

    public static byte[] BuildRequest() { var r = new byte[65]; r[3] = 0x02; r[4] = 0x02; r[6] = 0x83; return r; } // [0] = identifiant de rapport 0

    /// <summary>Décode une réponse, avec ou sans l'octet d'identifiant de rapport en tête.</summary>
    public static (Kind Result, int Percent, bool Charging) Decode(byte[] r)
    {
        byte status, chg, level;
        if (r.Length >= 9 && r[4] == 0x02 && r[6] == 0x83) { status = r[1]; chg = r[7]; level = r[8]; }
        else if (r.Length >= 8 && r[3] == 0x02 && r[5] == 0x83) { status = r[0]; chg = r[6]; level = r[7]; }
        else return (Kind.None, -1, false);
        if (status == 0xA0) return (Kind.Asleep, -1, false);
        if (status != 0xA1 && status != 0xA2) return (Kind.None, -1, false);
        if (level > 100) return (Kind.None, -1, false);
        return (Kind.Battery, level, chg == 0x01);
    }

    static (Kind Result, int Percent, bool Charging) Query(HidDev d)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!d.SetFeature(BuildRequest())) return (Kind.None, -1, false);
            for (int read = 0; read < 3; read++)
            {
                Thread.Sleep(read == 0 ? 100 : 60);
                if (!d.GetFeature(0x00, out var r)) return (Kind.None, -1, false);
                var rep = Decode(r);
                if (rep.Result != Kind.None) return rep;
            }
        }
        return (Kind.None, -1, false);
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFFFF && c.FeatLen == 65).OrderByDescending(c => c.Usage == 0).ToList();
            if (colls.Count == 0) continue;
            bool handled = false;
            foreach (var c in colls)
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                var (res, pct, chg) = Query(d);
                if (res == Kind.None) continue;
                // Une clé partagée entre câble et récepteur : brancher le câble met à jour l'entrée existante sur place.
                ushort keyPid = m.ReceiverPid != 0 ? m.ReceiverPid : m.Pid;
                var s = new DeviceStatus { Key = "arbiter:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(keyPid), Name = m.Name, Kind = DeviceKind.Mouse, Via = m.Receiver ? "2.4 GHz receiver" : "USB cable" };
                if (res == Kind.Asleep) s.Connected = false;
                else
                {
                    s.Connected = pct > 0 || !m.Receiver; // 0 d'un récepteur = souris éteinte ou hors de portée
                    if (s.Connected) s.Percent = pct;
                    s.Charging = chg;
                }
                ctx.Add(s, Vid, m.Pid);
                if (m.ReceiverPid != 0) ctx.Claim(Vid, m.ReceiverPid);
                handled = true;
                break;
            }
            // Le matériel Akitsu présent est pris même muet pour que LamzuDriver ne le nomme pas à tort sur 373E.
            if (!handled) ctx.Claim(Vid, m.Pid);
        }
    }
}
