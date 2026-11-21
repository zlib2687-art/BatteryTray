// CHERRY MX 3.0S Wireless (récepteur 046A:00EA ou câble 046A:00E3) et récepteur de souris CHERRY XTRFY 1K (046A:0330).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/cherry.cpp, porté en C# — voir NOTICE.md.
// Clavier : canal vendeur de 64 octets, page FF1C, usage 0092 (trame et commande de B1Chat/BatteryWatch, MIT, comme AkkoDriver) :
//   requête : type 0x1A, charge 03 00 00 55 ; réponse : [0] 4, [3] 0x1A, [8] pourcentage, [9] non nul sur alimentation USB.
// Souris XTRFY : canal vendeur de 17 octets, page FF02, usage 0002, trames « CompX » (rapport 08, somme de contrôle = 0x55 − somme des 16 premiers octets) :
//   03 = état de la liaison ([6] non nul = souris connectée) ; 04 = alimentation ([6] %, [7] en charge, [8..9] millivolts, grand-boutiste).
//   Exemple capturé (95 % / 4084 mV) : 08 04 00 00 00 02 5f 00 0f f4 00 00 00 00 00 00 e5. Deux requêtes de lecture seulement.
namespace BatteryTray.Providers.Hid;

public static class CherryDriver
{
    const ushort Vid = 0x046A, KeyboardPage = 0xFF1C, KeyboardUsage = 0x0092, XtrfyPid = 0x0330;
    const int KbLen = 64, XtrfyLen = 17;
    const byte KbReportId = 0x04, KbStatusType = 0x1A, XtrfyReportId = 0x08, XtrfyLink = 0x03, XtrfyPower = 0x04;

    // Câble d'abord : branché, le clavier est alimenté et éveillé alors que le récepteur peut aussi être présent.
    static readonly (ushort Pid, string Via)[] KbModels = { (0x00E3, "USB cable"), (0x00EA, "2.4 GHz receiver") };

    public static byte[] BuildKeyboardQuery() => AkkoDriver.BuildQuery();
    public static bool IsKeyboardReply(byte[] r) => r.Length >= 10 && r[0] == KbReportId && r[3] == KbStatusType;

    public static byte[] BuildXtrfyQuery(byte cmd)
    {
        var f = new byte[XtrfyLen];
        f[0] = XtrfyReportId; f[1] = cmd;
        f[XtrfyLen - 1] = XtrfyChecksum(f);
        return f;
    }

    static byte XtrfyChecksum(byte[] f) { int sum = 0; for (int i = 0; i + 1 < f.Length; i++) sum += f[i]; return (byte)(0x55 - sum); }

    public static bool XtrfyChecksumOk(byte[] r) => r.Length == XtrfyLen && XtrfyChecksum(r) == r[XtrfyLen - 1];

    public static bool IsXtrfyReply(byte[] r, byte cmd) => r.Length == XtrfyLen && r[0] == XtrfyReportId && r[1] == cmd && XtrfyChecksumOk(r);

    /// <summary>Réponse « alimentation » : (pourcentage ou -1, en charge, millivolts).</summary>
    public static (int Percent, bool Charging, int Millivolts) ParseXtrfyPower(byte[] r) => (r[6] <= 100 ? r[6] : -1, r[7] != 0, (r[8] << 8) | r[9]);

    static byte[]? KeyboardQuery(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        var q = BuildKeyboardQuery();
        for (int attempt = 0; attempt < 2; attempt++) // le récepteur ne répond que clavier éveillé
        {
            if (!d.Write(q, 300)) return null;
            long end = Environment.TickCount64 + 400;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = d.Read(left);
                if (r == null) break;
                if (IsKeyboardReply(r)) return r;
            }
        }
        return null;
    }

    static void PollKeyboard(PollContext ctx)
    {
        foreach (var m in KbModels)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c => c.Page == KeyboardPage && c.Usage == KeyboardUsage && c.InLen == KbLen && c.OutLen == KbLen))
            {
                var r = KeyboardQuery(c);
                if (r == null) continue; // clavier éteint ou en veille : rien à afficher
                var s = new DeviceStatus { Key = "cherry:mx30s", Name = "CHERRY MX 3.0S Wireless", Kind = DeviceKind.Keyboard, Via = m.Via }; // même entrée quelle que soit la connexion
                if (r[8] <= 100) { s.Percent = r[8]; s.Charging = r[9] != 0; } else s.Connected = false;
                ctx.Add(s, Vid, m.Pid);
                return;
            }
        }
    }

    static byte[]? XtrfyExchange(HidDev d, byte cmd)
    {
        d.Flush();
        if (!d.Write(BuildXtrfyQuery(cmd), 300)) return null;
        long end = Environment.TickCount64 + 500;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var f = d.Read(left);
            if (f == null) return null;
            if (IsXtrfyReply(f, cmd)) return f;
        }
    }

    static void PollXtrfy(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, XtrfyPid)) return;
        foreach (var c in ctx.Find(Vid, XtrfyPid, c => c.Page == 0xFF02 && c.Usage == 0x0002 && c.InLen == XtrfyLen && c.OutLen == XtrfyLen))
        {
            using var d = new HidDev();
            if (!d.Open(c)) return;
            bool answered = false, connected = true; int pct = -1; bool charging = false;
            var link = XtrfyExchange(d, XtrfyLink);
            if (link != null) { answered = true; connected = link[6] != 0; }
            byte[]? p = (link == null || connected) ? XtrfyExchange(d, XtrfyPower) : null;
            if (p != null) { answered = true; connected = true; var w = ParseXtrfyPower(p); pct = w.Percent; charging = w.Charging; }
            if (!answered) return; // silence : souris éteinte ou en veille
            if (!connected) { ctx.Claim(Vid, XtrfyPid); return; }
            var s = new DeviceStatus { Key = "cherry:xtrfy", Name = "Cherry XTRFY wireless mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct, Charging = charging };
            if (pct < 0) s.Connected = false; // liée mais sans réponse d'alimentation exploitable
            ctx.Add(s, Vid, XtrfyPid);
            return;
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollKeyboard(ctx);
        PollXtrfy(ctx);
    }
}
