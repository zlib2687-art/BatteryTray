// Claviers sur le « Wireless 2.4G Dongle » partagé 36B0:3002, et claviers PMO Hub sur leur câble USB. Le récepteur transmet le canal HID brut du clavier sur l'interface 1, page FF60 / usage 61, rapports d'entrée et de sortie de
// 33 octets (identifiant 0 + 32 octets). Le câble expose la même collection avec des rapports de 65 octets. Deux familles de micrologiciels y répondent :
// 1. Micrologiciel PMO Hub (PMO / WOBKEY Wave 75 RGB, Aurora 80, PMO P75, PMO 80). Trames [00] AA <cmd> <décalage lo> <décalage hi> <long> 00 00… ; les réponses répètent AA <cmd> <décalage>, avec 55 (OK) ou 0F (erreur) à l'octet 7.
//    Une session entoure la lecture : START_COMM AA 10 00 00 <long> (long = charge − 8 : 0x18 sur le récepteur, 0x38 sur le câble ; réponse du récepteur … 55 00 B0 36 09 30 : le clavier appairé est 36B0:3009 en petit-boutiste) ;
//    GET_BATTERY AA 30 00 00 18 (réponse … 55 <%> ; sans START il répond 0F) ; STOP_COMM AA 11 00 00 <long> (réponse … 55). L'octet après le pourcentage est lu comme drapeau de charge par pmohub.cn, mais il restait 00 sur un
//    Wave 75 en charge : non utilisé. Quand le clavier est sur son câble, le récepteur expire. Sources : rétro-ingénierie de pmohub.cn par somebody2506 (issue 343), testé sur un PMO Wave 75 RGB sur le récepteur et le câble
//    (78 %, comme la lecture Bluetooth), et le pilote web de pmohub.cn (protocole 1 : REPORT_ID 170, CMD_START_COMM 16, CMD_STOP_COMM 17, CMD_GET_BATTERY_STATUS 48 lu avec longueur 24, batterie à l'octet 8 de la réponse,
//    SUCCESS_FLAG 85, FAILURE_FLAG 15 ; PID de câble 3009 WAVE 75, 3004 Aurora 80, 302B PMO P75, 3012 PMO 80, avec 3002 pour leur récepteur 2,4G). Non testé sur Aurora 80, PMO P75 et PMO 80.
// 2. Micrologiciel VIA (URX Core75, EVO80 « EVO80 2.4G », issue 1406). La batterie est la valeur personnalisée VIA 7 sur le canal 0, lue par la commande de lecture VIA 0x08 : requête [00] 08 00 07 00… ; réponse 08 00 07 <niveau>…
//    (pas d'état de charge). Un micrologiciel sans cette valeur répond FF (« non géré » de VIA), ignoré. Source : HaloBattery (providers/urx.py et docs/protocols.md), tirée du pilote web d'URX HUB et confirmée sur un Core75 réel.
// Le micrologiciel derrière le récepteur n'est pas visible dans les descripteurs : on essaie d'abord le protocole probable (VIA si la chaîne produit nomme une carte URX, PMO Hub sinon), puis l'autre, puis on ne demande plus que celui
// qui a répondu. Un récepteur muet est réessayé avec un délai croissant. Les deux protocoles ne font que lire ; chaque micrologiciel ignore les trames de l'autre.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/via_dongle.cpp, porté en C# — voir NOTICE.md.
namespace BatteryTray.Providers.Hid;

public static class ViaDongleDriver
{
    const ushort Vid = 0x36B0, Dongle = 0x3002;

    public sealed record PmoBoard(ushort Pid, string Name);
    public static readonly PmoBoard[] PmoBoards = { new(0x3009, "WAVE 75"), new(0x3004, "Aurora 80"), new(0x302B, "PMO P75"), new(0x3012, "PMO 80") };

    public static string? PmoName(ushort pid) => PmoBoards.FirstOrDefault(b => b.Pid == pid)?.Name;

    public static bool IsRawHid(HidColl c) => c.Iface == 1 && c.Page == 0xFF60 && c.Usage == 0x0061 && c.InLen == c.OutLen && (c.OutLen == 33 || c.OutLen == 65);

    public static byte[] PmoFrame(int payload, byte cmd, byte len) { var f = new byte[1 + payload]; f[1] = 0xAA; f[2] = cmd; f[5] = len; return f; } // [0] = identifiant de rapport 0

    public static bool PmoOk(byte[] r) => r.Length > 8 && r[7] == 0x55;

    /// <summary>Réponse GET_BATTERY PMO : pourcentage (octet 8) si OK, sinon -1.</summary>
    public static int PmoLevel(byte[] bat) => PmoOk(bat) && bat[8] <= 100 ? bat[8] : -1;

    /// <summary>Clavier appairé annoncé par la réponse START du récepteur : octets 9..10 = 36B0, 11..12 = PID (petit-boutiste) ; 0 si absent.</summary>
    public static ushort PairedPid(byte[] start) => start.Length > 12 && (start[9] | (start[10] << 8)) == Vid ? (ushort)(start[11] | (start[12] << 8)) : (ushort)0;

    public static byte[] ViaRequest(int outLen) { var r = new byte[outLen]; r[1] = 0x08; r[2] = 0x00; r[3] = 0x07; return r; }

    /// <summary>Réponse VIA : niveau (1..100) ou -1.</summary>
    public static int ViaLevel(byte[] r) => r.Length > 3 && r[3] is >= 1 and <= 100 ? r[3] : -1;

    /// <summary>« EVO80 2.4G » → « EVO80 », « URX Core75 Wireless » → « URX Core75 » ; le nom générique du récepteur → « Wireless keyboard ».</summary>
    public static string ViaName(string name)
    {
        if (name.Contains("Dongle", StringComparison.Ordinal)) return "Wireless keyboard";
        foreach (var suffix in new[] { " 2.4G", " Wireless" }) if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return name.Length == 0 ? "Wireless keyboard" : name;
    }

    // Envoie une requête et attend la réponse qui commence par <echo> ; renvoie la charge (identifiant retiré) ou null.
    static byte[]? Ask(HidDev d, byte[] req, byte[] echo, int ms)
    {
        d.Flush();
        if (!d.Write(req)) return null;
        long end = Environment.TickCount64 + ms;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left); // l'identifiant 0 est retiré : r[0] est le premier octet de charge
            if (r == null) return null;
            if (r.Length < echo.Length + 6 || !r.AsSpan(0, echo.Length).SequenceEqual(echo)) continue; // p. ex. une poussée d'état
            return r;
        }
    }

    static int PmoQuery(HidDev d, out ushort paired)
    {
        paired = 0;
        int payload = d.Coll.OutLen - 1; byte len = (byte)(payload - 8); // 32 ou 64 ; 0x18 ou 0x38, comme pmohub.cn
        var startEcho = new byte[] { 0xAA, 0x10, 0x00, 0x00 };
        var start = Ask(d, PmoFrame(payload, 0x10, len), startEcho, 600) ?? Ask(d, PmoFrame(payload, 0x10, len), startEcho, 600); // la liaison se réveille
        if (start == null || !PmoOk(start))
        {
            // On ferme la session au cas où le START aurait été pris ; on n'attend pas quand rien ne répond.
            if (start == null) d.Write(PmoFrame(payload, 0x11, len)); else Ask(d, PmoFrame(payload, 0x11, len), new byte[] { 0xAA, 0x11, 0x00, 0x00 }, 300);
            return -1;
        }
        paired = PairedPid(start);
        var bat = Ask(d, PmoFrame(payload, 0x30, 0x18), new byte[] { 0xAA, 0x30, 0x00, 0x00 }, 600);
        int level = bat != null ? PmoLevel(bat) : -1;
        Ask(d, PmoFrame(payload, 0x11, len), new byte[] { 0xAA, 0x11, 0x00, 0x00 }, 600); // on termine toujours la session
        return level;
    }

    static int ViaQuery(HidDev d)
    {
        var req = ViaRequest(d.Coll.OutLen);
        for (int attempt = 0; attempt < 2; attempt++) // la première requête peut être manquée pendant le réveil de la liaison
        {
            var r = Ask(d, req, new byte[] { 0x08, 0x00, 0x07 }, 600);
            if (r != null) return ViaLevel(r);
        }
        return -1;
    }

    enum Proto { Unknown, Pmo, Via }
    sealed class State { public Proto Proto; public int Misses; public long NextTry; public ushort Paired; }
    static readonly Dictionary<ushort, State> States = new();

    static void Missed(State st)
    {
        st.Misses++;
        // Un protocole connu qui cesse de répondre signifie sans doute un clavier endormi (ou passé sur son câble) : on réessaie bientôt. Après plusieurs échecs, on l'oublie au cas où un autre clavier aurait été appairé.
        if (st.Misses >= 6) st.Proto = Proto.Unknown;
        int sec = st.Proto != Proto.Unknown ? 20 : Math.Min(300, 30 << Math.Min(st.Misses - 1, 4));
        st.NextTry = Environment.TickCount64 + sec * 1000L;
    }

    static void Answered(State st, Proto p) { st.Proto = p; st.Misses = 0; st.NextTry = 0; }

    static DeviceStatus Make(ushort keyPid, string name, string via, int level) =>
        new() { Key = "via:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(keyPid), Name = name, Kind = DeviceKind.Keyboard, Via = via, Percent = level }; // récepteur et câble partagent le PID du clavier

    static bool PollCable(PollContext ctx, ushort pid)
    {
        if (ctx.IsClaimed(Vid, pid)) return false;
        var colls = ctx.Find(Vid, pid, IsRawHid);
        if (colls.Count == 0) { States.Remove(pid); return false; } // débranché : on repart de zéro au retour
        if (!States.TryGetValue(pid, out var st)) States[pid] = st = new State();
        if (Environment.TickCount64 < st.NextTry) return false;
        foreach (var c in colls)
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int level = PmoQuery(d, out _);
            if (level < 0) continue;
            Answered(st, Proto.Pmo);
            ctx.Add(Make(pid, PmoName(pid) ?? "Wireless keyboard", "USB cable", level), Vid, pid);
            return true;
        }
        Missed(st);
        return false;
    }

    static void PollDongle(PollContext ctx, bool cableAnswered)
    {
        if (ctx.IsClaimed(Vid, Dongle)) return;
        var colls = ctx.Find(Vid, Dongle, c => IsRawHid(c) && c.OutLen == 33);
        if (colls.Count == 0) { States.Remove(Dongle); return; }
        if (!States.TryGetValue(Dongle, out var st)) States[Dongle] = st = new State();
        // Un clavier PMO sur son câble laisse le récepteur sans réponse (START_COMM expire) : on le saute.
        if (cableAnswered && st.Proto != Proto.Via) { ctx.Claim(Vid, Dongle); return; }
        if (Environment.TickCount64 < st.NextTry) return;
        var c = colls[0];
        using var d = new HidDev();
        if (!d.Open(c)) return;
        var order = new[] { Proto.Pmo, Proto.Via };
        if (st.Proto == Proto.Via || (st.Proto == Proto.Unknown && c.Product.Contains("URX", StringComparison.Ordinal))) order = new[] { Proto.Via, Proto.Pmo };
        int tries = st.Proto == Proto.Unknown ? 2 : 1; // une fois connu, on ne demande que le protocole qui a répondu
        for (int i = 0; i < tries; i++)
        {
            if (order[i] == Proto.Pmo)
            {
                int level = PmoQuery(d, out ushort paired);
                if (level < 0) continue;
                if (paired != 0) st.Paired = paired;
                Answered(st, Proto.Pmo);
                ushort keyPid = st.Paired != 0 ? st.Paired : Dongle;
                ctx.Add(Make(keyPid, PmoName(keyPid) ?? "Wireless keyboard", "2.4 GHz receiver", level), Vid, Dongle);
                return;
            }
            int lv = ViaQuery(d);
            if (lv < 0) continue;
            Answered(st, Proto.Via);
            ctx.Add(Make(Dongle, ViaName(c.Product), "2.4 GHz receiver", lv), Vid, Dongle);
            return;
        }
        Missed(st);
    }

    public static void Poll(PollContext ctx)
    {
        bool cable = false;
        foreach (var b in PmoBoards) cable |= PollCable(ctx, b.Pid);
        PollDongle(ctx, cable);
    }
}
