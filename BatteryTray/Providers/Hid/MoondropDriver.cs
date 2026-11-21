// Casque sans fil MOONDROP GM01 Pro (35D8:0133, puce WalkPlay).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/moondrop.cpp, porté en C# — voir NOTICE.md.
// Récepteur 2,4 GHz : expose le canal de contrôle WalkPlay sur l'interface 3, collection de page grand public, rapports de 64 octets, identifiant 4B. Commandes [4B][sens][cmd][long][données…] ;
// sens 80 = lecture, 01 = écriture : seules des lectures sont envoyées. Le récepteur transmet la plupart des lectures au casque : sans lien elles restent sans réponse.
//   [4B][80][27][00] → [4B][80][27][06][00][00][00][00][lien][%] : [8] lien = 01 casque allumé et appairé ; [9] % (aligné sur Moondrop Link, 50 %).
//   [4B][80][31][00] → [4B][80][31][01][lien] : répondu par le récepteur lui-même ; 01 casque lié, 00 une fois passé sur son câble USB.
// Câble USB : le casque s'énumère avec le même 35D8:0133 mais l'interface 3 n'a alors qu'une collection de contrôle grand public (pas de &colNN dans le chemin) et pas de canal de contrôle : aucun niveau
// à lire. Branché au PC il charge : il est signalé « en charge » et l'appli garde le dernier niveau. Sur câble il coupe la liaison 2,4 GHz.
// Vérifié sur matériel (micrologiciel 1.5.3). Non vu : un indicateur de charge sur chargeur mural en 2,4 GHz. Trames : jeromeof/devicePEQ (walkplayHidHandler.js) ; la commande batterie a été trouvée par un balayage en lecture seule.
namespace BatteryTray.Providers.Hid;

public static class MoondropDriver
{
    const ushort Vid = 0x35D8, Gm01Pro = 0x0133; // WalkPlay est partagé par d'autres DAC / récepteurs : seuls des identifiants connus
    const byte Report = 0x4B, Read = 0x80, CmdBattery = 0x27, CmdLink = 0x31;

    public static byte[] BuildRead(byte cmd) => new byte[] { Report, Read, cmd, 0x00 };

    public static bool IsReply(byte[] r, byte cmd) => r.Length >= 5 && r[0] == Report && r[1] == Read && r[2] == cmd;

    /// <summary>Réponse batterie : pourcentage si lien actif et niveau plausible, sinon null.</summary>
    public static int? ParseBattery(byte[] r) => r.Length >= 10 && r[3] >= 6 && r[8] != 0 && r[9] <= 100 ? r[9] : null;

    /// <summary>Réponse « lien » : vrai si le récepteur indique que le casque n'est plus lié (passé sur câble ou éteint).</summary>
    public static bool IsUnlinked(byte[] r) => r.Length >= 5 && r[3] >= 1 && r[4] == 0;

    /// <summary>Windows n'ajoute &amp;colNN au chemin que pour les appareils à plusieurs collections de premier niveau : le récepteur en a quatre sur l'interface 3, le casque sur câble une.</summary>
    public static bool IsSingleCollection(string path) => !path.ToLowerInvariant().Contains("&col");

    static byte[]? Query(HidDev d, byte cmd)
    {
        d.Flush();
        if (!d.Write(BuildRead(cmd))) return null;
        long end = Environment.TickCount64 + 400;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null || r.Length < 5) return null;
            if (IsReply(r, cmd)) return r;
        }
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Gm01Pro)) return;
        var s = new DeviceStatus { Key = "moondrop:" + HidUtil.Hex4(Gm01Pro), Name = "MOONDROP GM01 Pro", Kind = DeviceKind.Headset };
        bool found = false, dongleUnlinked = false;
        foreach (var c in ctx.Find(Vid, Gm01Pro, c => c.Iface == 3 && c.Page == 0x000C))
        {
            found = true;
            if (c.OutLen < 4 || c.InLen < 10) continue; // seulement le canal de contrôle du récepteur
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            if (Query(d, CmdBattery) is { } r && ParseBattery(r) is { } p) { s.Via = "2.4 GHz receiver"; s.Percent = p; ctx.Add(s, Vid, Gm01Pro); return; }
            if (Query(d, CmdLink) is { } l && IsUnlinked(l)) dongleUnlinked = true;
        }
        if (!found) return;
        bool onCable = ctx.Find(Vid, Gm01Pro, c => c.Iface == 3).Any(c => IsSingleCollection(c.Path));
        if (onCable) { s.Via = "USB cable"; s.Charging = true; ctx.Add(s, Vid, Gm01Pro); } // niveau inconnu sur câble ; l'appli garde le dernier
        else if (dongleUnlinked) { s.Via = "2.4 GHz receiver"; s.Connected = false; ctx.Add(s, Vid, Gm01Pro); } // casque éteint ou hors de portée
        else ctx.Claim(Vid, Gm01Pro); // aucune réponse : l'appli garde la dernière valeur
    }
}
