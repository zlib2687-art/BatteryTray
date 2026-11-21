// Souris Machenike L7 Arctic / L7 Mix (VID 248A : récepteur FF4C, câble FF4E).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/machenike.cpp, porté en C# — voir NOTICE.md.
// Protocole de lecture d'état de l'application officielle L7 Mix 1.0.0.8 (DeviceDriver.exe), vérifié sur un L7 Arctic par récepteur et par câble.
// Rapport de fonctionnalité 51, commande 06, 21 octets : [10] mode / liaison, [11] %, [12] en charge.
namespace BatteryTray.Providers.Hid;

public static class MachenikeDriver
{
    const ushort Vendor = 0x248A, Receiver = 0xFF4C, Cable = 0xFF4E;
    const int StatusLength = 21;

    public static bool IsStatusCollection(HidColl c, bool cable) =>
        c.Iface == (cable ? 2 : 1) && c.Page == 0x008C && c.Usage == 1 && c.InLen == 33 && c.OutLen == 33 && c.FeatLen == 1001;

    /// <summary>Décode la trame d'état comme l'application officielle : (connectée, pourcentage ou -1, en charge) ; null si ce n'est pas la réponse validée de cette commande.</summary>
    public static (bool Connected, int Percent, bool Charging)? DecodeStatus(byte[] r, bool cable)
    {
        if (r.Length != StatusLength || r[0] != 0x51 || r[1] != 0x06 || r[4] != 0x8A || r[5] != 0x24 || r[6] != 0x4E || r[7] != 0xFF || (r[10] & 7) != (cable ? 0 : 1)) return null;
        // Le bit de liaison sans fil est aussi à zéro sur câble : la connexion USB elle-même fait foi.
        bool connected = cable || (r[10] & 8) != 0;
        if (connected && (r[11] > 100 || r[12] > 1)) return null;
        return (connected, connected ? r[11] : -1, connected && r[12] == 1); // 0 % est valide, pas un marqueur de déconnexion
    }

    public static byte[] BuildRequest() { var r = new byte[StatusLength]; r[0] = 0x51; r[1] = 0x06; return r; }

    static (bool Connected, int Percent, bool Charging)? QueryStatus(HidDev d, bool cable)
    {
        long deadline = Environment.TickCount64 + 200;
        if (!d.SetFeature(BuildRequest())) return null;
        // La réponse doit remplacer la réponse précédente du récepteur. Une application du fabricant ouverte peut y laisser des réponses sans rapport : seule la trame validée est acceptée.
        do
        {
            Thread.Sleep(10);
            if (d.GetFeature(0x51, out var r) && DecodeStatus(r, cable) is { } s) return s;
        } while (Environment.TickCount64 < deadline);
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        bool haveCable = false;
        // Une lecture du câble en direct l'emporte sur l'état éventuellement en cache de la souris vu par le récepteur.
        foreach (ushort pid in new[] { Cable, Receiver })
        {
            if (ctx.IsClaimed(Vendor, pid)) continue;
            bool cable = pid == Cable;
            foreach (var c in ctx.Find(Vendor, pid, c => IsStatusCollection(c, cable)))
            {
                if (c.Product != "L7 Mix") continue;
                // Réservé même en cas d'échec d'E/S : un pilote Ajazz ultérieur partage le VID 248A et enverrait sinon sa commande différente à la collection du câble.
                ctx.Claim(Vendor, pid);
                if (haveCable) break;
                // 1001 est le plus grand rapport de cette collection (données de macros) ; le rapport 51 fait 21 octets : on utilise sa longueur exacte, comme l'application officielle.
                var status = new HidColl
                {
                    Path = c.Path, Product = c.Product, Vid = c.Vid, Pid = c.Pid, Iface = c.Iface, Page = c.Page, Usage = c.Usage,
                    InLen = c.InLen, OutLen = c.OutLen, FeatLen = StatusLength, Device = c.Device,
                };
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(status)) break;
                if (QueryStatus(d, cable) is not { } st) break;
                var s = new DeviceStatus
                {
                    Key = "machenike:l7", Name = "Machenike L7 Arctic / L7 Mix", Kind = DeviceKind.Mouse, // même entrée en passant du câble au récepteur
                    Via = cable ? "USB cable" : "2.4 GHz receiver", Connected = st.Connected, Percent = st.Percent, Charging = st.Charging,
                };
                haveCable = cable;
                ctx.Add(s, Vendor, pid);
                break; // une collection d'état exacte et une requête bornée par produit
            }
        }
    }
}
