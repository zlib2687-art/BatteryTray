// Claviers MCHOSE sur le récepteur 2,4 GHz commun actuel, VID 3837 PID 3033 (chaîne produit « MCHOSE <modèle> 2.4G » ; les G75 V2, G87 V2, K99 V3 et K87S en sont tous livrés). M HUB associe ce récepteur à deux protocoles
// selon le clavier : G75 V2 / G87 V2 / K99 V3 parlent des trames « GLW » sur la collection de 64 octets sans identifiant 0001:0000 (la collection FF31:0074 répond aux mêmes trames en secours), alors que le K87S parle
// le protocole « QHW » à rapport 0x4D derrière d'autres descripteurs. Ce pilote ne met en œuvre que GLW : un récepteur QHW ne répond jamais à ces trames et reste masqué au lieu d'afficher une entrée fantôme.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose_keyboard.cpp, porté en C# — voir NOTICE.md.
//   requête : rapport de sortie de 64 octets sans identifiant : [0]=0x55 drapeau [1]=commande [2]=0x00 [3]=somme [4]=taille [5]=décalage lo [6]=décalage hi [7]=0x00, complété de zéros ;
//             somme = (taille + décalage + charge) & 0xFF, les lectures n'ont pas de charge
//   réponse : rapport d'entrée au même en-tête, drapeau 0xAA, charge à partir de l'octet 8
//   batterie : lecture « func config » (commande 0x05, décalage 0, taille 56) : charge[32] bit 7 = en charge, bits 0..6 = % (85 % en décharge sur le G75 V2 de test)
// Protocole du pilote web M HUB de MCHOSE (classe GLW « Channel:Keyboard » : _simpleGetCommand / getFuncConfig / parseBaseFuncConfigStruct), confirmé sur un récepteur G75 V2 réel (3837:3033). Les poussées de batterie
// (FA FB 07 <%> <charge>) qu'écoute aussi M HUB n'ont jamais été observées sur ce récepteur : la valeur est donc interrogée. Comme le clavier ATK, le récepteur est interrogé au plus toutes les 10 minutes : le clavier décide
// seul de s'endormir et l'appli ne doit pas le tenir éveillé.
namespace BatteryTray.Providers.Hid;

public static class MchoseKeyboardDriver
{
    const ushort Vid = 0x3837, Pid = 0x3033;
    const byte Flag = 0x55, CmdFuncConfig = 0x05;
    const int Payload = 8, Battery = 32;
    const long AskIntervalMs = 10 * 60 * 1000;

    public static string DisplayName(string product)
    {
        const string suffix = " 2.4G"; // « MCHOSE G75 V2 2.4G » → « MCHOSE G75 V2 »
        if (product.Length > suffix.Length && product.EndsWith(suffix, StringComparison.Ordinal)) product = product[..^suffix.Length];
        return product.Length == 0 ? "MCHOSE keyboard" : product;
    }

    /// <summary>Lecture de 56 octets au décalage 0 : [identifiant 0] drapeau, commande, 0x00, somme, taille, décalage. La somme d'une lecture sans charge vaut taille + décalage.</summary>
    public static byte[] BuildRequest() => new byte[] { 0x00, Flag, CmdFuncConfig, 0x00, 56, 56, 0x00, 0x00, 0x00 };

    public static (int Percent, bool Charging)? ParseFuncConfig(byte[] r)
    {
        if (r.Length < Payload + Battery + 1) return null;
        if (r[0] != 0xAA || r[1] != CmdFuncConfig || r[5] != 0 || r[6] != 0) return null; // décalage 0
        int size = r[4];
        if (size < Battery + 1 || Payload + size > r.Length) return null;
        int sum = r[4] + r[5] + r[6] + r[7];
        for (int i = 0; i < size; i++) sum += r[Payload + i];
        if ((sum & 0xFF) != r[3]) return null;
        int pct = r[Payload + Battery] & 0x7F;
        return pct <= 100 ? (pct, (r[Payload + Battery] >> 7) != 0) : null;
    }

    static (int Percent, bool Charging)? QueryBattery(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) Thread.Sleep(150);
            if (!d.Write(BuildRequest())) continue;
            long end = Environment.TickCount64 + 500;
            while (end - Environment.TickCount64 > 0)
            {
                var r = d.Read((int)(end - Environment.TickCount64));
                if (r == null) break;
                if (ParseFuncConfig(r) is { } p) return p;
            }
        }
        return null;
    }

    static long _lastAsk; static int _lastPercent = -1; static bool _lastCharging;

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        var cs = ctx.Find(Vid, Pid, c => c.InLen == 65 && c.OutLen == 65 && ((c.Page == 0x0001 && c.Usage == 0x0000) || (c.Page == 0xFF31 && c.Usage == 0x0074))); // filtre de M HUB pour ce récepteur ; la seconde répond aux mêmes trames
        if (cs.Count == 0) return; // récepteur absent
        long now = Environment.TickCount64;
        bool ask = _lastAsk == 0 || now - _lastAsk >= AskIntervalMs;
        int percent = -1; bool charging = false;
        if (ask)
        {
            _lastAsk = now;
            foreach (var c in cs) if (QueryBattery(c) is { } p) { percent = p.Percent; charging = p.Charging; break; }
        }
        if (percent < 0 && !ask) { percent = _lastPercent; charging = _lastCharging; } // entre deux demandes : on garde la lecture précédente
        if (percent < 0 && _lastPercent < 0) return; // jamais répondu : récepteur QHW (K87S) ou liaison morte
        if (percent >= 0) { _lastPercent = percent; _lastCharging = charging; }
        var s = new DeviceStatus { Key = "mchose:3837:3033", Name = DisplayName(cs[0].Product), Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver" };
        if (percent >= 0) { s.Percent = HidUtil.Clamp100(percent); s.Charging = charging; }
        else s.Connected = false; // demandé sans réponse : clavier éteint, endormi ou occupé ; on garde la dernière valeur
        ctx.Add(s, Vid, Pid);
    }
}
