// Scyrox V12M sur son récepteur 2,4 GHz 395A:0006 (issue #1380 de BatteryHub). Seul cet identifiant est traité : les V6S / V8S du même identifiant fabricant (récepteur 000E, câble 000B / 000C)
// répondent à une autre commande d'état (2E 00 03), et les V6 / V8 plus anciens sont chez Compx (3554).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/scyrox.cpp, porté en C# — voir NOTICE.md.
// Lecture de profil en lecture seule d'après le pilote web S-Center 2.0 de Scyrox, sur la collection vendeur FF00:0002 de l'interface 2 (rapports d'entrée et de sortie de 64 octets) :
//   rapport de sortie 08 : [08][01][00…][somme] (somme = octet de poids faible de 08 + 01 = 09 dans le dernier octet).
//   Le récepteur répond par un flot de rapports d'entrée 08 de 64 octets (une trame 08 01 par profil plus des trames d'axes 23 / 25, terminé par une trame 08 01 avec [3] = 0). La trame du profil actif a
//   [1] = 01, [3] ≠ 0, [4] = profil actif = [5], [7] = nombre de crans de DPI 1..5, [62] = 0 (1 = erreur), et le pourcentage de batterie en [20].
//   Aucun champ de charge confirmé par les sources : la charge n'est pas affichée.
// Sources : late-autumn-gif/mousedock (MIT) ReferenceBatteryProtocol.cs / ScyroxV12Dpi.cs, MoFeng2223/BrimDeck (Scyrox.cs). Rien n'écrit de réglage : la commande 01 ne fait que lire les profils.
namespace BatteryTray.Providers.Hid;

public static class ScyroxDriver
{
    const ushort Vid = 0x395A, V12mReceiver = 0x0006;

    public static byte[] BuildRequest() { var r = new byte[64]; r[0] = 0x08; r[1] = 0x01; r[63] = 0x09; return r; }

    public enum Kind { Skip, End, Battery }

    /// <summary>Classe une trame du flot : « Skip » (trame d'axe, autre profil, erreur), « End » (fin du flot ou écho de la requête, ou niveau invalide), « Battery » (profil actif).</summary>
    public static (Kind Result, int Percent) ParseFrame(byte[] r)
    {
        if (r.Length != 64 || r[0] != 0x08 || r[1] != 0x01) return (Kind.Skip, -1);
        if (r[3] == 0) return (Kind.End, -1);
        if (r[62] == 1 || r[4] > 3 || r[4] != r[5] || r[7] < 1 || r[7] > 5) return (Kind.Skip, -1); // pas le profil actif
        return r[20] > 100 ? (Kind.End, -1) : (Kind.Battery, r[20]);
    }

    static int ReadBattery(HidDev d)
    {
        d.Flush();
        if (!d.Write(BuildRequest())) return -1;
        long end = Environment.TickCount64 + 1500; int pct = -1;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) break;
            var r = d.Read(left);
            if (r == null) break;
            var (kind, p) = ParseFrame(r);
            if (kind == Kind.Skip) continue;
            if (kind == Kind.Battery) pct = p;
            break;
        }
        d.Flush(); // on jette le reste du flot de profils
        return pct;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, V12mReceiver)) return;
        foreach (var c in ctx.Find(Vid, V12mReceiver, c => c.Page == 0xFF00 && c.Usage == 0x0002 && c.OutLen == 64 && c.InLen == 64))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int pct = ReadBattery(d);
            if (pct < 0) continue; // souris endormie / éteinte ou pas de réponse valide
            ctx.Add(new DeviceStatus { Key = "scyrox:" + HidUtil.Hex4(V12mReceiver), Name = "Scyrox V12M", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct }, Vid, V12mReceiver);
            break;
        }
    }
}
