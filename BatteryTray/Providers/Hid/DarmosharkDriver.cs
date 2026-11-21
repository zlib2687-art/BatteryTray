// Souris Darmoshark M3 / M3S sur leurs récepteurs Telink (VID 248A : FF30 → M3, FF31 → M3S) et M3S Pro sur son récepteur Nordic « 1K NRF Receiver » (1915:0723, vérifié sur matériel).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/darmoshark.cpp, porté en C# — voir NOTICE.md.
// M3 / M3S : référence Barba2k2/darmoshark-m3-configurator (PROTOCOL.md). Rapport de fonctionnalité 0x51 ; SetFeature [51][opcode] (03 = liaison, 06 = identification), la réponse est relue par
//   GetFeature 51 (≥ 12 octets, [0] = 51, [1] = opcode). La liaison doit répondre vid 248A / pid de la souris, octet 7 non nul ; l'identification : [2] = 1, vid / pid aux octets 4..7, [11] = %.
//   Un rapport d'entrée 54 E4 02 signale que le récepteur renonce. La requête de liaison remplace toute réponse batterie précédente dans le tampon du récepteur.
// M3S Pro : relevé sur une capture USB du logiciel Darmoshark. Collection vendeur : interface 3, page FF0A, rapports de 64 octets sans identifiant. Les 64 octets d'une trame totalisent 0xA1 (mod 256).
//   Requête [41][00][81][01] 0… [somme] ; réponse [41][00][8C][01][..][..][%]….
namespace BatteryTray.Providers.Hid;

public static class DarmosharkDriver
{
    const ushort Vid = 0x248A, NrfVid = 0x1915, NrfReceiver = 0x0723;
    const byte Feature = 0x51, Bond = 0x03, Identify = 0x06;

    public sealed record Model(ushort Receiver, ushort Mouse, string Name);
    public static readonly Model[] Models = { new(0xFF30, 0xFF12, "Darmoshark M3"), new(0xFF31, 0xFF18, "Darmoshark M3S") };

    public static ushort ReadId(byte[] reply, int offset) => (ushort)(reply[offset] | (reply[offset + 1] << 8));

    /// <summary>Réponse à la requête de liaison : vrai si la souris attendue est appairée (vid, pid, état non nul).</summary>
    public static bool IsBonded(byte[] reply, ushort mouse) => reply.Length >= 12 && ReadId(reply, 3) == Vid && ReadId(reply, 5) == mouse && reply[7] != 0;

    /// <summary>Réponse à l'identification : pourcentage, ou -1.</summary>
    public static int ParseIdentify(byte[] reply, ushort mouse) =>
        reply.Length >= 12 && reply[2] == 1 && ReadId(reply, 4) == Vid && ReadId(reply, 6) == mouse && reply[11] <= 100 ? reply[11] : -1;

    public static bool IsGiveUp(byte[] input) => input.Length >= 3 && input[0] == 0x54 && input[1] == 0xE4 && input[2] == 2;

    public static byte NrfChecksum(byte[] frame)
    {
        int sum = 0; for (int i = 0; i + 1 < 64; i++) sum += frame[i];
        return (byte)((0xA1 - sum) & 0xFF);
    }

    public static byte[] BuildNrfRequest()
    {
        var req = new byte[65]; req[1] = 0x41; req[3] = 0x81; req[4] = 0x01; // [0] = identifiant de rapport 0
        req[64] = NrfChecksum(req[1..]);
        return req;
    }

    /// <summary>Réponse M3S Pro : pourcentage ou -1 (en-tête, somme et niveau vérifiés).</summary>
    public static int ParseNrf(byte[] reply) =>
        reply.Length >= 64 && reply[0] == 0x41 && reply[2] == 0x8C && reply[3] == 0x01 && reply[63] == NrfChecksum(reply) && reply[6] <= 100 ? reply[6] : -1;

    static byte[]? Query(HidDev d, byte opcode, long receiverDeadline)
    {
        long start = Environment.TickCount64;
        if (start >= receiverDeadline) return null;
        long deadline = Math.Min(receiverDeadline, start + 500);
        d.Flush();
        if (Environment.TickCount64 >= deadline || !d.SetFeature(new byte[] { Feature, opcode })) return null;
        for (long now = Environment.TickCount64; now < deadline; now = Environment.TickCount64)
        {
            var input = d.Read((int)Math.Min(20, deadline - now));
            if (input != null && IsGiveUp(input)) return null;
            if (Environment.TickCount64 >= deadline) return null;
            if (d.GetFeature(Feature, out var reply) && reply.Length >= 12 && reply[0] == Feature && reply[1] == opcode) return Environment.TickCount64 < deadline ? reply : null;
        }
        return null;
    }

    static int ReadBattery(HidDev d, ushort mouse, long deadline)
    {
        // La requête de liaison remplace toute réponse batterie précédente dans le tampon du récepteur avant d'en demander une autre.
        if (Query(d, Bond, deadline) is not { } bond || !IsBonded(bond, mouse)) return -1;
        return Query(d, Identify, deadline) is { } id ? ParseIdentify(id, mouse) : -1;
    }

    static void PollModel(PollContext ctx, Model m)
    {
        if (ctx.IsClaimed(Vid, m.Receiver)) return;
        foreach (var c in ctx.Find(Vid, m.Receiver, c => c.Iface == 1 && c.Page == 0x008C && c.Usage == 1 && c.InLen == 33 && c.OutLen == 33 && c.FeatLen == 1001))
        {
            long deadline = Environment.TickCount64 + 1000;
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            // On garde du temps pour relire la liaison et réessayer si la première requête reste sans réponse.
            int pct = ReadBattery(d, m.Mouse, Math.Min(deadline, Environment.TickCount64 + 300));
            if (pct < 0 && Environment.TickCount64 < deadline) pct = ReadBattery(d, m.Mouse, deadline);
            if (pct < 0) continue;
            ctx.Add(new DeviceStatus { Key = ctx.StableKey("darmoshark:" + HidUtil.Hex4(m.Receiver)), Name = m.Name, Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct }, Vid, m.Receiver);
            return;
        }
    }

    static void PollNrf(PollContext ctx)
    {
        if (ctx.IsClaimed(NrfVid, NrfReceiver)) return;
        foreach (var c in ctx.Find(NrfVid, NrfReceiver, c => c.Iface == 3 && c.Page == 0xFF0A && c.Usage == 1 && c.InLen == 65 && c.OutLen == 65))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(BuildNrfRequest())) continue;
            long deadline = Environment.TickCount64 + 600; int pct = -1;
            for (long now = Environment.TickCount64; now < deadline && pct < 0; now = Environment.TickCount64)
            {
                var r = d.Read((int)Math.Min(100, deadline - now));
                if (r != null && r.Length >= 64) pct = ParseNrf(r);
            }
            if (pct < 0) continue;
            ctx.Add(new DeviceStatus { Key = "darmoshark:" + HidUtil.Hex4(NrfVid) + ":" + HidUtil.Hex4(NrfReceiver), Name = "Darmoshark M3S Pro", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct }, NrfVid, NrfReceiver);
            return;
        }
    }

    public static void Poll(PollContext ctx) { foreach (var m in Models) PollModel(ctx, m); PollNrf(ctx); }
}
