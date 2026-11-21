// Souris sans fil MCHOSE sur le récepteur 2,4 GHz ou le câble USB (VID 5253) : L7 / L7 Pro / L7 Ultra, M7 / M7 Ultra, A7 / A7 Ultra, K7 (le PID du récepteur est 1020 pour plusieurs : le modèle ne se déduit pas du PID, la chaîne
// produit sert de nom). Le même protocole tourne sur les récepteurs Realtek ODM de VID 3837 (A5 V2 Ultra 3837:100C vérifié sur matériel ; A7 V2 Ultra+ 3837:100B demandé dans l'issue 227 ; 1001 / 100A / 1012 apparaissent comme produits
// MCHOSE dans les projets de protocole). Les autres produits 3837 (G3 V2, claviers) utilisent d'autres protocoles et ne sont volontairement pas sondés.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose.cpp, porté en C# — voir NOTICE.md.
//   requête : rapport de fonctionnalité de 65 octets sur la collection vendeur FF01:0001, identifiant 0x12, charge [06 00 00…] avec CHAQUE octet de charge inversé (^ 0xFF), remplissage compris
//   réponse : le même rapport relu : [12][F9 = 06 ^ FF][charge, inversée] ; charge = vid:u16 (5253 ; 41E4 ou 3837 sur les récepteurs 3837), modèle:u16, micrologiciel:u32, drapeaux:u8 (09 sur le récepteur, 00 sur le câble), niveau:u8, charge:u8
// Le récepteur n'a une valeur que tant que la souris elle-même est éveillée : endormie, il répète la commande mais avec une charge entièrement nulle, signalée « endormie » (la dernière valeur est conservée). La requête doit être
// renvoyée à chaque tentative de lecture ; relire sans demander renvoie la requête elle-même. Protocole : alexfrih/mchose-linux (PROTOCOL.md, tiré du pilote M HUB de MCHOSE), confirmé sur un L7 Ultra (5253:1020) et un A5 V2 Ultra (3837:100C).
namespace BatteryTray.Providers.Hid;

public static class MchoseDriver
{
    const ushort Vid = 0x5253, VidRealtek = 0x3837;
    // Identifiants de récepteurs 3837 vus en pratique, gardés explicites pour que claviers et G3 V2 du même identifiant ne soient jamais sondés avec ce protocole.
    static readonly ushort[] RealtekPids = { 0x100C, 0x100B, 0x100A, 0x1001, 0x1012 };
    const byte Report = 0x12, CmdStatus = 0x06;

    public static byte[] BuildRequest(int featLen) { var r = new byte[Math.Max(featLen, 65)]; Array.Fill(r, (byte)0xFF); r[0] = Report; r[1] = CmdStatus ^ 0xFF; return r; }

    public enum Kind { Invalid, Asleep, Status }

    /// <summary>1 = réponse d'état, 0 = écho valide à charge vide (souris endormie), -1 sinon.</summary>
    public static (Kind Result, int Level, bool Charging, int Flags) ParseStatus(byte[] r, ushort vidField, ushort altVidField)
    {
        if (r.Length < 13 || r[0] != Report || r[1] != (CmdStatus ^ 0xFF)) return (Kind.Invalid, 0, false, 0);
        var p = new byte[11];
        for (int i = 0; i < 11; i++) p[i] = (byte)(r[2 + i] ^ 0xFF);
        if (p.All(b => b == 0)) return (Kind.Asleep, 0, false, 0);
        ushort field = (ushort)(p[0] | (p[1] << 8));
        if (field != vidField && field != altVidField) return (Kind.Invalid, 0, false, 0);
        return p[9] <= 100 ? (Kind.Status, p[9], p[10] != 0, p[8]) : (Kind.Invalid, 0, false, 0);
    }

    static void PollProduct(PollContext ctx, ushort vid, ushort pid, ushort vidField, ushort altVidField)
    {
        foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF01 && c.Usage == 0x0001 && c.FeatLen >= 65))
        {
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(c)) continue;
            var req = BuildRequest(c.FeatLen);
            Kind state = Kind.Invalid; bool got = false; (Kind Result, int Level, bool Charging, int Flags) best = default;
            for (int attempt = 0; attempt < 5 && state != Kind.Status; attempt++)
            {
                if (attempt > 0) Thread.Sleep(100);
                if (!d.SetFeature(req)) break;
                Thread.Sleep(120);
                if (!d.GetFeature(Report, out var r)) continue;
                var st = ParseStatus(r, vidField, altVidField);
                if (st.Result > state || (!got && st.Result != Kind.Invalid)) { if (st.Result != Kind.Invalid) { state = st.Result; best = st; got = true; } }
            }
            if (!got) continue; // ne répond pas à ce protocole
            var s = new DeviceStatus { Key = "mchose:" + HidUtil.Hex4(pid), Name = c.Product.Length > 0 ? c.Product : "MCHOSE mouse", Kind = DeviceKind.Mouse };
            if (best.Result == Kind.Status) { s.Percent = HidUtil.Clamp100(best.Level); s.Charging = best.Charging; s.Via = best.Flags == 0 && best.Charging ? "USB cable" : "2.4 GHz receiver"; }
            else { s.Connected = false; s.Via = "2.4 GHz receiver"; }
            ctx.Add(s, vid, pid);
            break;
        }
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid)) PollProduct(ctx, vid, pid, Vid, Vid);
        foreach (ushort pid in RealtekPids)
        {
            if (ctx.IsClaimed(VidRealtek, pid)) continue;
            // Le champ vid diffère selon les récepteurs 3837 : 41E4 sur l'A5 V2 Ultra (100C), 3837 sur le K7 Ultra (1001).
            PollProduct(ctx, VidRealtek, pid, 0x41E4, VidRealtek);
        }
    }
}
