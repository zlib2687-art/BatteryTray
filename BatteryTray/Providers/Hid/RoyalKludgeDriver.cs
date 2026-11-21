// Souris sans fil Royal Kludge (RK) MG6 Pro, identifiant fabricant 2E3C.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/royal_kludge.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote web de RK (drive.rkgaming.com) : les entrées MG6 Pro ont protocolType « QiWang », dont getDeviceBattery() construit une trame de 64 octets non chiffrée envoyée en rapport de sortie 0 sur la collection vendeur :
//   [0] type de message 1 (TYPE_CFG_REMOTE_PARAM : transmis à la souris) ; [1] type de config principal 1 (CFG_MAIN_TYPE_SYS_CTRL), longueur de données 0 dans le quartet haut ; [2] sous-type 2 (CFG_SYS_CTRL_GET_BATTER_VOL) ;
//   [3] octet de propriété, bit 0 = accès LECTURE. La réponse a les trois mêmes premiers octets ; [3] bit 4 est access_res (1 = la souris a répondu), [4..5] tension (mV, petit-boutiste), [6..7] pourcentage, [8..9] température, [10] USB branché.
// Le pilote web ignore les réponses avec access_res 0 ; ici elles s'affichent « endormie » (le récepteur a répondu pour une souris qui ne l'a pas fait).
// Identifiants du même fichier : 00A6 / 00E6 = récepteur 2,4 GHz (page vendeur FF00:0001), 00A5 / 00E5 = câble USB (page vendeur FFF2:0001).
namespace BatteryTray.Providers.Hid;

public static class RoyalKludgeDriver
{
    public static bool IsCable(ushort pid) => pid is 0x00A5 or 0x00E5;

    public static byte[] BuildRequest() => new byte[] { 0x00, 0x01, 0x01, 0x02, 0x01 }; // identifiant de rapport 0 puis la trame

    public enum Kind { NotBattery, Offline, Online, Invalid }

    /// <summary>Analyse un rapport d'entrée : « NotBattery » (autre notification), « Offline » (access_res 0), « Invalid » (&gt; 100 %), « Online » avec (%, mV, USB branché).</summary>
    public static (Kind Result, int Percent, int Millivolts, bool Plugged) ParseReply(byte[] r)
    {
        if (r.Length < 11 || (r[0] & 0x1F) != 0x01 || (r[1] & 0x0F) != 0x01 || r[2] != 0x02) return (Kind.NotBattery, -1, 0, false);
        if (((r[3] >> 4) & 1) == 0) return (Kind.Offline, -1, 0, false);
        int p = r[6] | (r[7] << 8);
        return p > 100 ? (Kind.Invalid, -1, 0, false) : (Kind.Online, p, r[4] | (r[5] << 8), r[10] != 0);
    }

    static bool PollMg6(PollContext ctx, ushort vid, ushort pid)
    {
        ushort page = IsCable(pid) ? (ushort)0xFFF2 : (ushort)0xFF00;
        foreach (var c in ctx.Find(vid, pid, c => c.Page == page && c.Usage == 1 && c.OutLen == 65 && c.InLen == 65))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            bool answered = false; (int Percent, int Mv, bool Plugged)? online = null;
            for (int attempt = 0; attempt < 3 && online == null; attempt++)
            {
                if (attempt > 0) Thread.Sleep(100);
                d.Flush();
                if (!d.Write(BuildRequest())) break;
                // Le récepteur pousse aussi d'autres notifications sur cette collection : on saute jusqu'à la réponse de batterie.
                long end = Environment.TickCount64 + 600;
                while (end - Environment.TickCount64 > 0)
                {
                    var r = d.Read((int)(end - Environment.TickCount64));
                    if (r == null || r.Length < 11) break;
                    var p = ParseReply(r);
                    if (p.Result == Kind.NotBattery) continue;
                    answered = true;
                    if (p.Result == Kind.Online) online = (p.Percent, p.Millivolts, p.Plugged);
                    break;
                }
            }
            if (!answered) continue;
            var s = new DeviceStatus { Key = "rk:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "RK MG6 Pro", Kind = DeviceKind.Mouse, Via = IsCable(pid) ? "USB cable" : "2.4 GHz receiver" };
            if (online is { } o) { s.Percent = o.Percent; s.Charging = o.Plugged || IsCable(pid); }
            else s.Connected = false; // le récepteur a répondu, pas la souris : endormie ou éteinte, la dernière valeur est conservée
            ctx.Add(s, vid, pid);
            return true;
        }
        return false;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (v, pid) in ctx.Products(0x2E3C))
            if (pid is 0x00A5 or 0x00A6 or 0x00E5 or 0x00E6) PollMg6(ctx, v, pid);
    }
}
