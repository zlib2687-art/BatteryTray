// Razer BlackShark : casques à protocole vendeur propre (pas le rapport de fonctionnalité de 90 octets des souris Razer).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/razer_headset.cpp, porté en C# — voir NOTICE.md.
// Pas encore porté : Barracuda X (2022), dont le récepteur relaie un flux de trames Airoha (à faire séparément).
//
// BlackShark V3 / V3 Pro (1532:0576/0577 V3 Pro, 0579/057A V3, 0A4E/0A55 V3 Pro pour Xbox), interface 5 :
// rapport de sortie 02 de 64 octets : [02][00][60][00 00][00][taille][00 00][dir][classe][sous][argc][args]… ; [62] = XOR des octets 0..61.
// GET batterie = classe 21 → réponse args [niveau %][en charge]. Le casque coupe sa liaison 2,4 GHz si on l'interroge toutes les quelques
// secondes : au plus une fois toutes les 5 minutes, la dernière lecture est réutilisée entre-temps.
// Protocole : OpenRazer PR n° 2794 (docs/v3_pro_protocol.md) et github.com/ReedME/omarchy-blackshark-v3.
//
// BlackShark V2 Pro (2020), récepteur 2,4 GHz 1532:0528 : rapport de fonctionnalité FF sur FF00:0001
// (requête : openrazer/openrazer ticket 1280 ; réponse : HeyOkay/HaloBattery providers/blackshark2020.py).
// BlackShark V2 Pro (2023), dongle HyperSpeed 1532:0555 : enveloppe « PA » (OpenRazer PR n° 2862, HaloBattery providers/blackshark.py).
// BlackShark V2 HyperSpeed, dongle 1532:0565 : HID vendeur MediaTek (justik13/razer-blackshark-v2-hyperspeed-webhid, MIT).
// BlackShark V3 X (1532:057D dongle, 057C câble) : rapport de fonctionnalité 07 (OpenRazer PR n° 2899, vérifié sur un dongle 057D).
// Lecture seule : seules des requêtes de batterie et de charge sont envoyées.
namespace BatteryTray.Providers.Hid;

public static class RazerHeadsetDriver
{
    const ushort Vid = 0x1532;
    const long MinIntervalMs = 5 * 60 * 1000;

    static byte Xor(byte[] b, int from, int toExclusive)
    {
        byte x = 0;
        for (int i = from; i < toExclusive && i < b.Length; i++) x ^= b[i];
        return x;
    }

    static DeviceStatus Headset(string key, string name, string via) => new() { Key = key, Name = name, Kind = DeviceKind.Headset, Via = via };

    // ------------------------------------------------------------------ BlackShark V3 / V3 Pro (interface 5)

    public static byte[] BuildV3Packet(byte dir, byte cls)
    {
        var b = new byte[64];
        b[0] = 0x02; b[2] = 0x60; b[6] = 0x04; // taille des données : dir, classe, sous, argc
        b[9] = dir; b[10] = cls;
        b[62] = Xor(b, 0, 62);
        return b;
    }

    public static bool IsV3Reply(byte[] r, byte cls) => r.Length >= 15 && r[0] == 0x02 && r[2] == 0x60 && r[10] == cls;

    public static string V3Name(ushort pid) =>
        pid is 0x0A4E or 0x0A55 ? "Razer BlackShark V3 Pro for Xbox" : pid is 0x0576 or 0x0577 ? "Razer BlackShark V3 Pro" : "Razer BlackShark V3";

    /// <summary>Le plus petit identifiant de chaque paire est le câble USB, le plus grand le récepteur (0579 câble / 057A récepteur).</summary>
    public static bool V3IsCable(ushort pid) => pid is 0x0576 or 0x0579 or 0x0A4E;

    public static DeviceStatus? ParseV3(ushort pid, byte[]? battery, byte[]? cable)
    {
        if (battery == null || battery.Length < 15 || battery[13] > 100) return null;
        bool cableCharging = cable is { Length: >= 14 } && cable[13] == 1;
        var s = Headset("blackshark:" + HidUtil.Hex4(pid), V3Name(pid), V3IsCable(pid) ? "USB cable" : "2.4 GHz receiver");
        s.Percent = battery[13];
        s.Charging = battery[14] != 0 || cableCharging;
        return s;
    }

    // ------------------------------------------------------------------ BlackShark V3 X (rapport de fonctionnalité 07)

    public static byte[] BuildV3xRequest(byte txid, byte cmd)
    {
        var req = new byte[64];
        req[0] = 0x07; req[2] = txid; req[6] = 0x02; req[7] = 0x07; req[8] = cmd;
        byte crc = 0;
        for (int i = 3; i <= 60; i++) crc ^= req[i];
        req[62] = crc;
        return req;
    }

    /// <summary>Réponse à une requête V3 X : (valide, valeur). Statut 02 = succès, 01 = occupé ; autre (casque éteint) = échec.</summary>
    public static (bool Matches, bool Success, byte Value) ParseV3xReply(byte[] r, byte txid, byte cmd)
    {
        if (r.Length < 63 || r[2] != txid || r[7] != 0x07 || r[8] != cmd || r[6] < 2) return (false, false, 0);
        if (r[1] != 0x02 && r[1] != 0x01) return (true, false, 0);
        return (true, true, r[10]);
    }

    // ------------------------------------------------------------------ BlackShark V2 HyperSpeed (MediaTek)

    public static byte[] BuildHsQuery(byte cmd, byte seq)
    {
        var b = new byte[64];
        b[0] = 0x02; b[2] = seq; b[6] = 0x04; b[9] = 0x80; b[10] = cmd;
        b[62] = Xor(b, 0, 62);
        return b;
    }

    /// <summary>La couche HID Win32 renvoie l'identifiant de rapport dans r[0] : les décalages de WebHID sont décalés d'un octet.</summary>
    public static int ParseHsReply(byte[] r, byte cmd, byte seq)
    {
        if (r.Length < 64 || r[0] != 0x02 || r[2] != seq || r[10] != cmd || r[12] < 1) return -1;
        if (Xor(r, 1, 62) != r[62]) return -1;
        return r[13];
    }

    // ------------------------------------------------------------------ BlackShark V2 Pro (2020)

    static readonly byte[] V2Pro2020Request = { 0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05 };
    static readonly byte[] V2Pro2020Fresh = { 0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05 };
    static readonly byte[] V2Pro2020Stale = { 0xFF, 0x01, 0x00, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05 };

    static bool StartsWith(byte[] r, byte[] prefix) => r.Length >= prefix.Length && r.Take(prefix.Length).SequenceEqual(prefix);

    /// <summary>
    /// Réponse du récepteur : « fraîche » (préfixe connu) ou « périmée » (casque éteint, valable seulement à la dernière lecture).
    /// Le récepteur peut passer à 80 % dès que la charge commence, même depuis 10 % : en charge, le niveau reste inconnu.
    /// </summary>
    public static DeviceStatus? ParseV2Pro2020(byte[] r, bool lastRead)
    {
        if (r.Length != 64) return null;
        bool fresh = StartsWith(r, V2Pro2020Fresh);
        bool offline = lastRead && StartsWith(r, V2Pro2020Stale);
        if (!fresh && !offline) return null;
        var s = Headset("blackshark:" + HidUtil.Hex4(0x0528), "Razer BlackShark V2 Pro (2020)", "2.4 GHz receiver");
        int millivolts = fresh ? (r[12] << 8) | r[13] : 0;
        s.Connected = millivolts != 0;
        if (s.Connected)
        {
            s.Charging = r[11] == 0x09;
            if (!s.Charging && r[14] <= 100) s.Percent = r[14];
        }
        return s;
    }

    // ------------------------------------------------------------------ BlackShark V2 Pro (2023) : enveloppe « PA »

    public static byte[] BuildPaFrame(byte totalLen, byte innerLen, byte type, byte cmd, byte flag)
    {
        var b = new byte[64];
        b[0] = 0x02; b[1] = 0x80; b[2] = totalLen; b[5] = (byte)'P'; b[6] = (byte)'A'; b[7] = innerLen; b[9] = type; b[10] = cmd; b[11] = flag;
        return b;
    }

    /// <summary>Valeur d'une réponse « PA » à la commande <paramref name="cmd"/> (accusée, au moins un octet de données), ou -1.</summary>
    public static int ParsePaReply(byte[] r, byte cmd) =>
        r.Length >= 16 && r[0] == 0x02 && r[12] == cmd && r[13] == 0x01 && r[14] >= 1 ? r[15] : -1;

    // ------------------------------------------------------------------ échanges avec l'appareil

    static readonly Dictionary<ushort, (long At, DeviceStatus Status)> Cache = new();
    static byte _txid = 0x40, _hsSeq;

    static byte[]? V3Get(HidDev d, byte cls)
    {
        if (!d.Write(BuildV3Packet(0x80, cls))) return null;
        long end = Environment.TickCount64 + 600;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 15) return null;
            if (IsV3Reply(r, cls)) return r;
        }
        return null;
    }

    static void PollV3(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x0576, 0x0577, 0x0579, 0x057A, 0x0A4E, 0x0A55 })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            var colls = ctx.Find(Vid, pid, c => c.Iface == 5 && c.OutLen >= 64 && c.InLen >= 15);
            if (colls.Count == 0) continue;
            if (Cache.TryGetValue(pid, out var hit) && Environment.TickCount64 - hit.At < MinIntervalMs) { ctx.Add(hit.Status, Vid, pid); continue; }
            using var d = new HidDev();
            if (!d.Open(colls[0])) continue;
            // poignée de main attendue par le casque avant les lectures (init, état de charge sur les deux liaisons)
            d.Write(BuildV3Packet(0x00, 0x02)); d.Write(BuildV3Packet(0x00, 0x2A)); d.Write(BuildV3Packet(0x80, 0x2A));
            Thread.Sleep(250);
            d.Flush();
            var battery = V3Get(d, 0x21);
            if (battery == null) continue;
            var s = ParseV3(pid, battery, V3Get(d, 0x2A));
            if (s == null) continue;
            Cache[pid] = (Environment.TickCount64, s);
            ctx.Add(s, Vid, pid);
        }
    }

    static bool V3xGet(HidDev d, byte cmd, out byte value)
    {
        value = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            byte txid = ++_txid;
            if (!d.SetFeature(BuildV3xRequest(txid, cmd))) return false;
            Thread.Sleep(60);
            if (!d.GetFeature(0x07, out var r) || r.Length < 63) continue;
            var p = ParseV3xReply(r, txid, cmd);
            if (!p.Matches) continue;
            if (!p.Success) return false;
            value = p.Value;
            return true;
        }
        return false;
    }

    static void PollV3X(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x057D, 0x057C })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            var colls = ctx.Find(Vid, pid, c => c.Iface == 0 && c.FeatLen >= 64);
            if (colls.Count == 0) continue;
            if (Cache.TryGetValue(pid, out var hit) && Environment.TickCount64 - hit.At < MinIntervalMs) { ctx.Add(hit.Status, Vid, pid); continue; }
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(colls[0])) continue;
            if (!V3xGet(d, 0x80, out byte level) || level > 100) continue;
            V3xGet(d, 0x84, out byte charging);
            var s = Headset("blackshark:" + HidUtil.Hex4(pid), "Razer BlackShark V3 X", pid == 0x057D ? "2.4 GHz receiver" : "USB cable");
            s.Percent = level; s.Charging = charging == 1;
            Cache[pid] = (Environment.TickCount64, s);
            ctx.Add(s, Vid, pid);
        }
    }

    static int HsQuery(HidDev d, byte cmd)
    {
        byte seq = (byte)(0x60 | (_hsSeq++ & 0x1F));
        d.Flush();
        if (!d.Write(BuildHsQuery(cmd, seq))) return -1;
        long end = Environment.TickCount64 + 650;
        while (Environment.TickCount64 < end)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            int v = ParseHsReply(r, cmd, seq);
            if (v >= 0) return v;
        }
        return -1;
    }

    static void PollV2HyperSpeed(PollContext ctx)
    {
        const ushort pid = 0x0565; // dongle 2,4 GHz, pas la variante filaire 056E
        if (ctx.IsClaimed(Vid, pid)) return;
        var colls = ctx.Find(Vid, pid, c => (c.Page == 0xFF14 || c.Page == 0xFF00) && c.Usage == 0x01 && c.OutLen >= 64 && c.InLen >= 64);
        // on préfère l'interface de contrôle vendeur pour ne pas toucher aux points d'accès audio/téléphonie
        foreach (var c in colls.OrderByDescending(c => c.Page == 0xFF14).Where(c => c.Page is 0xFF14 or 0xFF00))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int level = HsQuery(d, 0x21);
            if (level < 0 || level > 100) continue;
            int charging = HsQuery(d, 0x2A);
            var s = Headset("blackshark:" + HidUtil.Hex4(pid), "Razer BlackShark V2 HyperSpeed", "2.4 GHz receiver");
            s.Percent = level; s.Charging = charging > 0;
            ctx.Add(s, Vid, pid);
            return;
        }
    }

    static void PollV2Pro2020(PollContext ctx)
    {
        const ushort pid = 0x0528; const int reportLen = 64, maxReads = 5;
        if (ctx.IsClaimed(Vid, pid)) return;
        foreach (var c in ctx.Find(Vid, pid, c => c.Iface == 3 && c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen == reportLen))
        {
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(c)) continue;
            if (!d.SetFeature(V2Pro2020Request)) continue;
            for (int read = 0; read < maxReads; read++)
            {
                Thread.Sleep(100); // Synapse attend environ 100 ms que le récepteur récupère l'état du casque
                if (!d.GetFeature(0xFF, out var r) || r.Length != reportLen) continue;
                var s = ParseV2Pro2020(r, read == maxReads - 1);
                if (s == null) continue;
                ctx.Add(s, Vid, pid);
                return;
            }
        }
    }

    static bool PaRemote(HidDev d, bool on)
    {
        bool ok = d.Write(BuildPaFrame(0x07, 0x0E, 0x02, 0xE1, (byte)(on ? 1 : 0)));
        Thread.Sleep(35);
        return ok;
    }

    /// <summary>Interroge une valeur ; renvoie le premier octet de données, ou -1 si le casque ne répond pas.</summary>
    static int PaQuery(HidDev d, byte cmd)
    {
        d.Flush();
        PaRemote(d, true); // sacrificiel : réveille une liaison assoupie
        int value = -1;
        for (int attempt = 0; attempt < 3 && value < 0; attempt++)
        {
            PaRemote(d, true);
            if (!d.Write(BuildPaFrame(0x08, 0x08, 0x03, cmd, 0))) break;
            long end = Environment.TickCount64 + 150;
            while (end - Environment.TickCount64 > 0)
            {
                var r = d.Read((int)(end - Environment.TickCount64));
                if (r == null || r.Length < 16) break;
                int v = ParsePaReply(r, cmd);
                if (v >= 0) { value = v; break; }
            }
        }
        PaRemote(d, false);
        return value;
    }

    static void PollV2Pro2023(PollContext ctx)
    {
        const ushort pid = 0x0555;
        if (ctx.IsClaimed(Vid, pid)) return;
        foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFF00 && c.OutLen >= 64 && c.InLen >= 16))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int level = PaQuery(d, 0x21);
            if (level < 0 || level > 100) continue;
            int charging = PaQuery(d, 0x2A);
            var s = Headset("blackshark:" + HidUtil.Hex4(pid), "Razer BlackShark V2 Pro (2023)", "2.4 GHz receiver");
            s.Percent = level; s.Charging = charging > 0;
            ctx.Add(s, Vid, pid);
            return;
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollV2HyperSpeed(ctx);
        PollV2Pro2020(ctx);
        PollV2Pro2023(ctx);
        PollV3X(ctx);
        PollV3(ctx);
    }
}
