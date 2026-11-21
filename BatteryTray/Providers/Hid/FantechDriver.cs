// Souris sans fil Fantech Helios Go XD5 (MCU Areson / SinoWealth, capteur PixArt PAW3370) et Redragon Invader Pro.
// VID 25A7 : câble USB-C FA7B, récepteur 2,4 GHz FA7C, Redragon FA7E.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/fantech.cpp, porté en C# — voir NOTICE.md.
// L'interface de configuration vendeur utilise des rapports de fonctionnalité de 17 octets, identifiant 0x08 :
//   [08][cmd][0 × 14][somme de contrôle], la somme des 17 octets valant 0x55 modulo 256.
//   cmd 04 = lire la batterie → réponse [6] = pourcentage, [7] = en charge. Aucune poignée de main : la souris répond à un cmd 04 simple.
// Le rapport de fonctionnalité et la réponse sont sur des collections HID différentes de l'interface 1 : la commande est écrite sur une
// poignée et toutes les collections d'entrée sont surveillées. Une souris en veille ne répond pas.
// Disposition confirmée sur matériel (FA7C) et sur les captures de libratbag (issue #1893). Protocole : github.com/jarjut/xd5-go-web,
// déduit d'OemDrv.exe de Fantech.
namespace BatteryTray.Providers.Hid;

public static class FantechDriver
{
    const ushort Vid = 0x25A7, PidWired = 0xFA7B, PidDongle = 0xFA7C, PidRedragon = 0xFA7E;
    const byte ReportId = 0x08;
    const int PacketLen = 17;

    public static byte[] MakePacket(byte cmd)
    {
        var p = new byte[PacketLen];
        p[0] = ReportId; p[1] = cmd;
        int sum = 0;
        for (int i = 0; i < PacketLen - 1; i++) sum += p[i];
        p[16] = (byte)(0x55 - sum);
        return p;
    }

    public readonly record struct Reply(int Percent, bool Charging, bool ChecksumOk);

    /// <summary>
    /// Lit une réponse « batterie ». Selon la pile HID, l'identifiant de rapport reste ou non dans le tampon : l'octet de commande est
    /// accepté à l'indice 0 ou 1 (les deux dispositions WebHID documentées par le projet d'origine).
    /// </summary>
    public static Reply? ParseBattery(byte[] r)
    {
        int sum = 0;
        for (int i = 0; i < r.Length && i < PacketLen; i++) sum += r[i];
        bool chk = r.Length >= PacketLen && (sum & 0xFF) == 0x55;

        Reply? Plausible(int power, int charge)
        {
            if (r.Length <= charge) return null;
            int p = r[power], c = r[charge];
            return p < 1 || p > 100 || c > 1 ? null : new Reply(p, c != 0, chk);
        }

        if (r.Length >= 7 && r[0] == 0x04)
        {
            if (Plausible(5, 6) is { } a) return a;
            if (Plausible(4, 5) is { } b) return b;
        }
        if (r.Length >= 8 && r[1] == 0x04) return Plausible(6, 7);
        return null;
    }

    static void Remember(Reply c, ref Reply? found)
    {
        if (found == null || (c.ChecksumOk && !found.Value.ChecksumOk)) found = c;
    }

    static Reply? QueryBattery(HidDev cmd, List<HidDev> readers)
    {
        cmd.Flush();
        if (!cmd.SetFeature(MakePacket(0x04))) return null;
        Reply? found = null;
        long end = Environment.TickCount64 + 500;
        while (end - Environment.TickCount64 > 0)
        {
            foreach (var d in readers)
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = d.Read(Math.Min(left, 40));
                if (r == null || r.Length < 6) continue;
                if (ParseBattery(r) is { } c) Remember(c, ref found);
            }
            if (found is { ChecksumOk: true }) break;
        }
        // Certains micrologiciels répondent par le rapport de fonctionnalité plutôt que par un rapport d'entrée.
        if (cmd.GetFeature(ReportId, out var f) && ParseBattery(f) is { } fc) Remember(fc, ref found);
        return found;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (pid != PidWired && pid != PidDongle && pid != PidRedragon) continue;
            var all = ctx.Find(vid, pid);
            var feat = all.FirstOrDefault(c => c.FeatLen >= PacketLen);
            if (feat == null) continue;
            using var cmd = new HidDev();
            if (!cmd.Open(feat) && !cmd.OpenFeatureOnly(feat)) continue;

            // Le pilote du fabricant lit le rapport d'entrée 08 sur une autre collection que celle où il écrit : on écoute toutes les collections d'entrée.
            var opened = new List<HidDev>();
            try
            {
                var readers = new List<HidDev>();
                if (feat.InLen >= 6) readers.Add(cmd);
                foreach (var c in all)
                {
                    if (c == feat || c.InLen < 6) continue;
                    var d = new HidDev();
                    if (d.Open(c)) { opened.Add(d); readers.Add(d); } else d.Dispose();
                }
                var found = QueryBattery(cmd, readers);
                if (found == null) continue;
                int pct = pid == PidRedragon ? HidUtil.Clamp100(found.Value.Percent * 10) : found.Value.Percent;
                ctx.Add(new DeviceStatus
                {
                    Key = (pid == PidRedragon ? "redragon:" : "fantech:") + HidUtil.Hex4(pid),
                    Name = pid == PidRedragon ? "Redragon Invader Pro" : "Fantech Helios Go XD5",
                    Kind = DeviceKind.Mouse, Via = pid == PidWired ? "USB cable" : "2.4 GHz receiver",
                    Percent = pct, Charging = found.Value.Charging,
                }, vid, pid);
                break;
            }
            finally { foreach (var d in opened) d.Dispose(); }
        }
    }
}
