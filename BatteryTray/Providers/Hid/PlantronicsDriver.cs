// Casques Plantronics / Poly (Voyager 8200 UC) derrière l'adaptateur USB Bluetooth BT600 (047F:02EE / 02F7).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/plantronics.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT ; confirmé sur matériel), lui-même issu de plantronics_bt600.hpp de HeadsetControl.
// Rapport 07 sur la collection vendeur FFA2:0003, 64 octets, complété de zéros :
//   07 01 01 10 <long> <adr> 00 00 <op> <grp> <élément> <valeur…> ; adr 00 = l'adaptateur, 20 = le casque ; les réponses portent 00 (adaptateur) / 02 (casque).
// L'adaptateur exige d'abord la séquence d'attache (rapport de sortie 06 01, un HELLO à l'adaptateur, un HELLO au casque). Puis GET groupe 0A élément 1A : <niveau> <nombre de niveaux> <en charge>
// <minutes hi> <minutes lo>, et le pourcentage vaut niveau × 100 / (nombre − 1). Seule la batterie est lue ; aucun réglage n'est écrit.
namespace BatteryTray.Providers.Hid;

public static class PlantronicsDriver
{
    const ushort Vid = 0x047F;
    const byte Report = 0x07, AddrDongle = 0x00, AddrHeadset = 0x20, ReplyDongle = 0x00, ReplyHeadset = 0x02;
    const byte OpHello = 0x01, OpGet = 0x02, OpHelloAck = 0x08, OpGetReply = 0x03;
    const byte GrpHello = 0x01, ItemHello = 0x02, GrpBattery = 0x0A, ItemBattery = 0x1A;

    public static byte[] Frame(byte op, byte grp, byte item, byte addr)
    {
        var f = new byte[64];
        new byte[] { Report, 0x01, 0x01, 0x10, (byte)(op == OpGet ? 0x06 : 0x07), addr, 0x00, 0x00, op, grp, item }.CopyTo(f, 0);
        return f;
    }

    public static bool IsReply(byte[] r, byte op, byte grp, byte item, byte addr) =>
        r.Length > 10 && r[0] == Report && r[1] == 0x01 && r[2] == 0x01 && r[3] == 0x10 && r[5] == addr && r[9] == grp && r[10] == item && r[8] == op;

    /// <summary>Réponse GET batterie : (pourcentage, en charge) ; null si l'échelle n'est pas celle de l'adaptateur (&lt; 2 niveaux ou niveau ≥ nombre).</summary>
    public static (int Percent, bool Charging)? ParseBattery(byte[] r)
    {
        if (r.Length < 14) return null;
        int level = r[11], count = r[12];
        if (count < 2 || level >= count) return null;
        return (level * 100 / (count - 1), r[13] != 0);
    }

    // Attend la réponse à (op, grp, item) en ignorant les évènements ; faux après 16 rapports ou 1 s de silence.
    static byte[]? Await(HidDev d, byte op, byte grp, byte item, byte addr)
    {
        for (int i = 0; i < 16; i++)
        {
            var r = d.Read(1000);
            if (r == null || r.Length == 0) return null;
            if (IsReply(r, op, grp, item, addr)) return r;
        }
        return null;
    }

    static (int Percent, bool Charging)? ReadBattery(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        d.Write(0x06, 0x01); // le coup de pouce d'attache de l'adaptateur ; sans effet s'il l'est déjà
        d.Write(Frame(OpHello, GrpHello, ItemHello, AddrDongle));
        Await(d, OpHelloAck, GrpHello, ItemHello, ReplyDongle);
        if (!d.Write(Frame(OpHello, GrpHello, ItemHello, AddrHeadset)) || Await(d, OpHelloAck, GrpHello, ItemHello, ReplyHeadset) == null) return null; // casque éteint, hors de portée ou non appairé
        if (!d.Write(Frame(OpGet, GrpBattery, ItemBattery, AddrHeadset)) || Await(d, OpGetReply, GrpBattery, ItemBattery, ReplyHeadset) is not { } r) return null;
        return ParseBattery(r);
    }

    public static void Poll(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x02EE, 0x02F7 })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFFA2 && c.Usage == 0x0003))
            {
                if (ReadBattery(c) is not { } b) continue;
                ctx.Add(new DeviceStatus { Key = "plantronics:" + HidUtil.Hex4(pid), Name = "Poly / Plantronics headset", Kind = DeviceKind.Headset, Via = "BT600 adapter", Percent = HidUtil.Clamp100(b.Percent), Charging = b.Charging }, Vid, pid);
                break;
            }
        }
    }
}
