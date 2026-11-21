// Casques Turtle Beach Stealth 700 Gen 3 (VID 10F5) sur l'émetteur USB ou le câble USB du casque.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/turtlebeach.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT) dont le chemin Stealth 700 Gen 3 vient de la rétro-ingénierie de Swarm II par @PaulRichez (halobattery-stealth700, docs/protocol.md, MIT ; testé sur l'émetteur
// Stealth 700X Gen 3 10F5:2251, le câble du casque 10F5:2253 répond à la même requête). lykosapps/Neap (FINDINGS.md, GPL-3.0 ; seules ses conclusions servent, pas de code) place les Stealth 700 PC,
// PlayStation et Xbox du catalogue de Swarm II dans la même famille de puces Airoha, avec le même canal de contrôle.
//   Collection vendeur FF13:0001 de l'interface 3. Hôte → appareil : rapport de sortie 06 de 62 octets, `06 <long16 LE> <octets RACE>` ; appareil → hôte : rapport d'entrée 07 encadré `07 <long> 00 <charge>`, lu par GetInputReport.
//   1. RACE 0x9942, active le client CoAP de la puce : 05 5A 02 00 42 99 (ce que Swarm II envoie à chaque lecture).
//   2. RACE 0x9902 portant un CoAP GET /GSI : 05 5A 0A 00 02 99 40 01 00 01 B3 47 53 49.
//   La réponse est un document JSON {"OR":"GSI","KVP":{…, "240":"<pourcentage>", …}}, réparti sur plusieurs rapports.
// Rien d'autre n'est envoyé et rien n'est modifié dans le casque. La charge n'est pas signalée : la clé 250 restait « 0 » câble branché (HaloBattery #211).
// 10F5:2262 (« Stealth 700PC Gen 3 », issue #1413) a la même collection FF13:0001 de 62 octets sur l'interface 3 : version PC du même casque, lue de la même façon (non testé sur matériel).
// L'émetteur non confirmé 10F5:2250 et les autres modèles (Stealth 600 Gen 3, Atlas Air, Stealth Pro II, lus par HaloBattery avec d'autres requêtes) ne sont pas activés.
using System.Text;

namespace BatteryTray.Providers.Hid;

public static class TurtleBeachDriver
{
    const ushort Vid = 0x10F5;

    public sealed record Model(ushort Pid, bool Cable);
    // Les émetteurs d'abord : le même casque n'est listé qu'une fois, par celui qui répond en premier.
    public static readonly Model[] Models = { new(0x2251, false), new(0x2262, false), new(0x2253, true) };

    public static readonly byte[] EnableCoap = { 0x05, 0x5A, 0x02, 0x00, 0x42, 0x99 };
    public static readonly byte[] GetGsi = { 0x05, 0x5A, 0x0A, 0x00, 0x02, 0x99, 0x40, 0x01, 0x00, 0x01, 0xB3, (byte)'G', (byte)'S', (byte)'I' };

    /// <summary>Rapport de sortie 06 de 62 octets : [06][longueur][00][octets RACE…].</summary>
    public static byte[] BuildSend(byte[] race)
    {
        var buf = new byte[62]; buf[0] = 0x06; buf[1] = (byte)race.Length; race.CopyTo(buf, 3); return buf;
    }

    /// <summary>Le « 240 » (pourcentage de batterie) le plus récent d'un document GSI dans le flot de charges utiles, ou -1.</summary>
    public static int FindLevel(string s)
    {
        if (!s.Contains("\"GSI\"", StringComparison.Ordinal)) return -1;
        int level = -1; const string key = "\"240\":\"";
        for (int p = s.IndexOf(key, StringComparison.Ordinal); p >= 0; p = s.IndexOf(key, p + 1, StringComparison.Ordinal))
        {
            int q = p + key.Length, v = 0, digits = 0;
            while (q < s.Length && s[q] is >= '0' and <= '9' && digits < 4) { v = v * 10 + (s[q++] - '0'); digits++; }
            if (digits > 0 && q < s.Length && s[q] == '"' && v <= 100) level = v;
        }
        return level;
    }

    /// <summary>Extrait la charge utile d'un rapport d'entrée 07 (`07 &lt;long&gt; 00 &lt;charge&gt;`) ; chaîne vide si ce n'en est pas un.</summary>
    public static string Payload(byte[] r)
    {
        if (r.Length < 3 || r[0] != 0x07) return "";
        int n = Math.Min((int)r[1], r.Length - 3);
        return Encoding.Latin1.GetString(r, 3, n);
    }

    public static string ModelName(string product)
    {
        if (product.Length == 0) return "Turtle Beach Stealth 700 Gen 3";
        return product.StartsWith("turtle beach", StringComparison.OrdinalIgnoreCase) ? product : "Turtle Beach " + product;
    }

    static void Drain(HidDev d, int n) { for (int i = 0; i < n; i++) d.GetInputReport(0x07, out _); }

    static int ReadLevel(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return -1;
        Drain(d, 5);
        if (!d.Write(BuildSend(EnableCoap))) return -1; // active le client CoAP
        Thread.Sleep(300); // la puce a besoin d'un instant une fois le client activé
        Drain(d, 5);
        if (!d.Write(BuildSend(GetGsi))) return -1;
        var payload = new StringBuilder();
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(30);
            if (!d.GetInputReport(0x07, out var r)) continue;
            payload.Append(Payload(r));
            int level = FindLevel(payload.ToString());
            if (level >= 0) return level;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        HidColl? found = null; Model? foundModel = null; int level = -1;
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen > 0 && c.OutLen > 0);
            if (colls.Count == 0) continue;
            if (found == null) { found = colls[0]; foundModel = m; }
            level = ReadLevel(colls[0]);
            if (level >= 0) { found = colls[0]; foundModel = m; break; }
        }
        if (found == null || foundModel == null) return;
        var s = new DeviceStatus { Key = "turtlebeach:stealth700", Name = ModelName(found.Product), Kind = DeviceKind.Headset, Via = foundModel.Cable ? "USB cable" : "2.4 GHz transmitter" }; // émetteur et câble = un seul casque
        if (level < 0) s.Connected = false; // pas de lecture (casque éteint ou Swarm II tient le canal) : on garde la dernière valeur
        else s.Percent = level;
        ctx.Add(s, Vid, foundModel.Pid);
        foreach (var o in Models) ctx.Claim(Vid, o.Pid);
    }
}
