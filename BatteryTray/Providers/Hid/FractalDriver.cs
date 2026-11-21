// Casque Fractal Design Scape (VID 36BC) : récepteur 2,4 GHz (PID 0001) ou casque sur son câble USB-C (PID 2001). Les deux exposent une collection
// vendeur de 64 octets, page FF00 / usage 01.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/fractal.cpp, porté en C# — voir NOTICE.md.
// Requêtes = rapport de sortie 0x02 : [02][type d'appareil][commande] ; réponse = rapport d'entrée 0x02 qui répète type et commande, puis la charge.
//   GET_DEVICE_STATE = 0x21, envoyé au type 0x11 (récepteur) ou 0xF1 (casque). Réponse du récepteur : charge[3] = 1 si le casque lui est lié.
//   Réponse du casque : charge[14] = niveau en %. Par le récepteur, la requête est relayée au casque par radio ; sur câble, il répond directement.
// Les décalages de la charge comptent depuis le type d'appareil (DataView WebHID, sans identifiant de rapport) : le tampon de type hidapi est décalé d'un octet.
// Source : le configurateur WebHID de Fractal (Adjust Pro) : classes BTDongle / Headset, getUpdatedDeviceState(). Adjust Pro ne lit pas d'indicateur de charge ;
// l'octet 22 de la charge vaut 1 sur câble USB et 0 par radio (vérifié sur un Scape), il sert de bit de charge. Sur son support de charge le casque se délie du récepteur : il apparaît éteint.
namespace BatteryTray.Providers.Hid;

public static class FractalDriver
{
    const byte ReportId = 0x02, TypeDongle = 0x11, TypeHeadset = 0xF1, GetDeviceState = 0x21;

    public static byte[] BuildRequest(byte type) => new byte[] { ReportId, type, GetDeviceState };

    /// <summary>Un rapport est la réponse à la requête si type et commande sont répétés et s'il fait au moins 24 octets.</summary>
    public static bool IsReply(byte[] r, byte type) => r.Length >= 24 && r[0] == ReportId && r[1] == type && r[2] == GetDeviceState;

    public static bool HeadsetLinked(byte[] dongleReply) => dongleReply.Length > 4 && dongleReply[4] == 1;

    /// <summary>Réponse du casque → (pourcentage, en charge) ; null si le niveau dépasse 100.</summary>
    public static (int Percent, bool Charging)? ParseHeadset(byte[] r) => r.Length < 24 || r[15] > 100 ? null : (r[15], r[23] == 1);

    static byte[]? GetState(HidDev d, byte type)
    {
        d.Flush();
        if (!d.Write(BuildRequest(type))) return null;
        long end = Environment.TickCount64 + 1000;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null) return null;
            if (IsReply(r, type)) return r;
        }
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x36BC))
        {
            if (pid != 0x0001 && pid != 0x2001) continue;
            bool cable = pid == 0x2001;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.OutLen >= 4 && c.InLen >= 24))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var s = new DeviceStatus { Key = "fractal:scape", Name = "Fractal Scape", Kind = DeviceKind.Headset, Via = cable ? "USB cable" : "2.4 GHz dongle" }; // même entrée sur récepteur et câble
                if (!cable)
                {
                    var dg = GetState(d, TypeDongle);
                    if (dg == null) continue;
                    if (!HeadsetLinked(dg)) { s.Connected = false; ctx.Add(s, vid, pid); break; } // récepteur présent, casque éteint, hors de portée ou sur son support
                }
                var hs = GetState(d, TypeHeadset);
                if (hs == null || ParseHeadset(hs) is not { } p) continue;
                s.Percent = p.Percent; s.Charging = p.Charging;
                ctx.Add(s, vid, pid);
                break;
            }
        }
    }
}
