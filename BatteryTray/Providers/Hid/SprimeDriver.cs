// Souris sans fil Sprime PM1 (identifiant Nordic 1915), sur son récepteur 2,4 GHz ou son câble USB.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/sprime.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote web de Sprime (sprime.pro) : la même requête d'état (rapport de fonctionnalité 05, commande 15) que le Ninjutso Sora V2 (voir NinjutsoDriver).
//   SET_FEATURE rapport 5, 31 octets de charge : [05][15][00][00][01][00…] ; GET_FEATURE rapport 5 : [05][15]… [9] %, [10] en charge, [11] complètement chargée, [12] en ligne.
//   Les indices comptent l'identifiant de rapport, comme le DataView renvoyé par WebHID.
// Identifiants relevés dans les filtres WebHID du pilote web : souris sur câble AC11 (noire) / AC12 (blanche) / AC13 (rouge) ; récepteurs AC1C et AD8C. Le chargeur d'amorçage (AC0B) n'est jamais touché.
namespace BatteryTray.Providers.Hid;

public static class SprimeDriver
{
    public static bool IsCable(ushort pid) => pid is 0xAC11 or 0xAC12 or 0xAC13;
    public static bool IsReceiver(ushort pid) => pid is 0xAC1C or 0xAD8C;

    /// <summary>Réponse : (connectée, pourcentage ou -1, en charge) ; null si invalide. Pleine = 100 % et pas en charge.</summary>
    public static (bool Connected, int Percent, bool Charging)? Parse(byte[] r, bool cable)
    {
        if (r.Length < 13 || r[0] != 0x05 || r[1] != 0x15 || r[9] > 100) return null;
        bool full = r[11] != 0;
        bool connected = cable || r[12] != 0; // récepteur présent, souris éteinte ou hors de portée
        return (connected, !connected ? -1 : full ? 100 : r[9], connected && r[10] != 0 && !full);
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x1915))
        {
            if (!IsCable(pid) && !IsReceiver(pid)) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFFA0 && c.Usage == 1 && c.FeatLen >= 32))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                bool cable = IsCable(pid);
                (bool Connected, int Percent, bool Charging)? res = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(90);
                    if (!d.SetFeature(NinjutsoDriver.BuildSoraQuery())) break;
                    Thread.Sleep(15);
                    if (!d.GetFeature(0x05, out var r) || Parse(r, cable) is not { } p) continue;
                    res = p;
                    if (p.Connected) break;
                }
                if (res == null) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "sprime:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "Sprime PM1", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver",
                    Connected = res.Value.Connected, Percent = res.Value.Percent, Charging = res.Value.Charging,
                }, vid, pid);
                break;
            }
        }
    }
}
