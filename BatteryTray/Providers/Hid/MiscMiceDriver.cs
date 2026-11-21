// Petites marques de souris, chacune avec un protocole de batterie documenté par la communauté.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/misc_mice.cpp, xenta_protocol.* et ardor_fury_v2_protocol.h, porté en C# — voir NOTICE.md.
//  * Motospeed X6 (Realtek VID 0BDA, récepteurs FFE0 / FFF2, interface 4) : rapport de sortie B5 [06], réponse rapport B4, octet 20 = niveau (+128 en charge). Le câble (FFF1) ne donne pas de batterie.
//    Source : Jacobz888/LGSTrayBattery-MotospeedX6.
//  * Récepteurs Xenta (Delux M900 Pro, Attack Shark X6 / R1 / X11 / X3 Max / V3 Pro / V6, Inphic ; VID 1D57) : le récepteur diffuse le rapport d'entrée 03 sur la page 000A toutes les ~2 s :
//    [03][modèle][40][état][%], état 2 = plein, 3 = en charge (pourcentage figé). Source : AeonArcHQ/omarchy-delux-m900. L'Attack Shark V6 (modèle 24) envoie 41 au lieu de 40 : les deux sont acceptés.
//    La collection ne fait que pousser (ni rapport de sortie ni de fonctionnalité) : ouverte en lecture seule. Une lecture compte quand deux trames concordent, car chaque liaison commence par un faux 100 %.
//  * Famille Ajazz AJ179 (récepteur VID 249A, câble 248A, interface 2) : rapport non numéroté de 32 octets, commande 10 (infos appareil) : réponse [10][00 = ok]…, [4..7] identifiant de modèle, [12] en charge,
//    [13] %, [14] souris connectée (récepteur), [31] = somme([4..30]). Source : Rockeyxx/aj179-linux (docs/PROTOCOL.md).
//  * Souris AJAZZ AJ-series sur l'identifiant SONiX 3151 (AJ159 PRO / APEX…) : collection de fonctionnalité de 65 octets, page FFFF / usage 02 ; SET_FEATURE (rapport 0x00, opcode 0xF7) fait monter la liaison de
//    télémétrie 2,4 GHz ; ~30 ms plus tard GET_FEATURE 0x05 donne la charge à l'octet 3 (0..100) ; une trame dont l'octet 1 ou 2 est non nul est un parasite de reconnexion. Source : Aiacos/ajazz-control-center.
//  * AJAZZ AJ139 V2 MC et apparentés sur le PID 2255 (VID A8A5 = récepteur, A8A4 = câble ; le PID est partagé avec K-snake X11 et NJ07/NJ08 V2 : nom générique). Collection FF01:0010, rapports de 64 octets : requête
//    [55][30][A5][chk][2E][00…], réponse [AA][30]…, octet 8 = %, octet 9 non nul sur alimentation USB. Les deux sources divergent sur l'octet 3 de la requête : les deux variantes sont essayées.
//    Sources : werquess/ajazz-aj139-macos et nico2525nn/AjazzAJ139V2.
//  * Incott G23 V2 / G24 sur l'identifiant PixArt 093A (522C = récepteur, 622C = câble), collection FF05:0001 : SET_FEATURE rapport 09 [09][89][00 × 7], puis GET_FEATURE 09 jusqu'à [09][89][batterie][dpi] ;
//    bit 7 = en charge, 7 bits bas = %. Une réponse dont les octets 2 et 3 sont nuls est périmée. Sur câble la première collection FF05 refuse SET_FEATURE : on essaie de la dernière à la première.
//    Sources : OmarK6/Incott-Manager, HeHaoren/incott-mouse-app, FelixNg21/MouseBattery.
//  * Delux M800 Pro (VID 248A, récepteur 5B2F, câble 5B2E) : rapport de fonctionnalité 0x0C de 33 octets, page FF00 / usage 01 : SET_FEATURE [0C][01][20][00][seq…] puis GET_FEATURE 0x0C : [8..11] modèle ASCII,
//    [18] %, [19] état (0 décharge, 1 charge, 2 plein). Le récepteur exige des numéros de séquence croissants à l'octet 4.
//  * Wraith W1 (Standard / Freestyle) sur les mêmes identifiants PixArt 093A:522C / 622C et collection FF05, mais d'autres commandes : 89 lit des réglages de profil, pas l'état. Rapports de fonctionnalité de 8 octets,
//    identifiant 09. Modèle d'abord : SET_FEATURE [09][8F] puis GET_FEATURE 09 jusqu'à l'écho de 8F ; octet 3 = modèle, 18 = W1 Standard, 20 = W1 Freestyle ; les modèles Incott ont d'autres codes (1, 2, 3, 6, 8, 9, 14) :
//    un W1 n'est jamais lu avec la requête Incott et inversement. État : 86 est une lecture par bouton ; la souris pousse son rapport d'état 09 [09][batterie][cadence | emplacement << 4]… en réponse. Après 8F, balayage
//    des boutons 0, 1, 2, 4, 3 une fois, puis battement de 700 ms (un 86 pour le bouton 0 relu) pendant l'attente du rapport. Batterie bit 7 = en charge, 7 bits bas = %. Source : pilote web Wraith (w1.software) et W1 Service v0.1.7.
namespace BatteryTray.Providers.Hid;

public static class MiscMiceDriver
{
    // ================================================================== logique pure (testée)

    /// <summary>Motospeed : octet 20 du rapport B4 → (pourcentage, en charge).</summary>
    public static (int Percent, bool Charging)? ParseMotospeed(byte[] r)
    {
        if (r.Length <= 20 || r[0] != 0xB4) return null;
        int raw = r[20]; bool chg = raw >= 128;
        return (HidUtil.Clamp100(chg ? raw - 128 : raw), chg);
    }

    /// <summary>Fury V2 d'ARDOR (récepteur FA60, usage 000A:0000) : 03 00 40 33 07 → 50 %, 03 00 40 73 08 → 75 % ; 25 % et 100 % déduits de la logique du fabricant. Le 5e octet change et n'est PAS une signature fixe.</summary>
    public static int ArdorFuryV2Percent(byte[] r)
    {
        if (r.Length < 5 || r[0] != 0x03 || r[1] != 0x00 || r[2] != 0x40 || (r[3] & 0x0F) != 0x03) return -1;
        return (r[3] >> 4) switch { 0x1 => 25, 0x3 => 50, 0x7 => 75, 0xF => 100, _ => -1 };
    }

    public sealed record XentaFrame(byte Model, byte Event, byte State, byte Level);

    public static XentaFrame? ParseXenta(byte[] b) =>
        b.Length < 5 || b[0] != 0x03 || (b[2] != 0x40 && b[2] != 0x41) ? null : new XentaFrame(b[1], b[2], b[3], b[4]);

    public sealed record XentaModel(byte Code, string Name, bool Tens);
    public static readonly XentaModel[] XentaModels =
    {
        new(0x73, "Inphic IN10R MAX", false), new(0x74, "Inphic IN10 MAX", false), new(0x3D, "Delux M900 Pro", false), new(0x30, "Delux M900 Pro", false), new(0x50, "Delux M900 Pro", false),
        new(0x55, "Attack Shark X11", false), new(0x4E, "Attack Shark X3 Max", false), new(0xE5, "Attack Shark V3 Pro", false), new(0x24, "Attack Shark V6", false), new(0x10, "Attack Shark X6 / R1", true),
    };

    /// <summary>Décide de l'état d'un récepteur Xenta d'après sa dernière trame. <paramref name="agreed"/> : les deux dernières trames sont identiques.</summary>
    public static DeviceStatus XentaStatus(ushort pid, XentaFrame f, bool agreed, int ardorPct, bool ardorActive, int ardorConfirmed)
    {
        bool cable = pid is 0xFA55 or 0x2155;
        var model = XentaModels.LastOrDefault(m => m.Code == f.Model);
        var s = new DeviceStatus { Key = "xenta:" + HidUtil.Hex4(pid), Name = model?.Name ?? "Wireless mouse", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver" };
        bool delux = pid == 0xFA65 && (f.Model == 0x30 || f.Model == 0x50);
        if (delux)
        {
            if (f.State == 0x02) s.Percent = 100;
            else if (f.State == 0x03) { s.Charging = true; if (f.Level <= 100) s.Percent = f.Level; }
            else if (agreed && f.State == 0x01 && f.Level is >= 1 and <= 100) s.Percent = f.Level;
        }
        else if (ardorPct >= 0)
        {
            s.Name = "ARDOR GAMING Fury V2";
            if (ardorActive) s.Percent = ardorConfirmed; // pendant un changement de palier, on montre la dernière valeur confirmée (validée après 2 rapports)
        }
        else if (cable || f.State == 0x02 || f.State == 0x03)
        {
            s.Charging = true; // le niveau est figé sur alimentation externe
            if (f.State == 0x02 && !cable) s.Percent = 100;
        }
        else if (agreed)
        {
            if (model is { Tens: true }) { if (f.Level is >= 1 and <= 10) s.Percent = f.Level * 10; }
            else if (f.State == 0x01 && f.Level is >= 1 and <= 100) s.Percent = f.Level;
        }
        if (s.Percent < 0 && !s.Charging) s.Connected = false; // affichée avec la dernière valeur connue
        return s;
    }

    /// <summary>Ajazz AJ179 : réponse à la commande 10 (somme [4..30] = octet 31) → (nom, %, en charge, connectée) ; null si invalide.</summary>
    public static (string Name, int Percent, bool Charging, bool MouseConnected)? ParseAj179(byte[] r)
    {
        if (r.Length < 32 || r[0] != 0x10 || r[1] != 0x00) return null;
        int sum = 0; for (int i = 4; i <= 30; i++) sum += r[i];
        if ((byte)sum != r[31] || r[13] > 100) return null;
        var model = new System.Text.StringBuilder();
        for (int i = 4; i < 8 && r[i] >= 0x20 && r[i] < 0x7F; i++) model.Append((char)r[i]);
        string m = model.ToString();
        return (m.Length == 4 && m[0] == 'M' ? "Ajazz AJ" + m[1..] : "Ajazz mouse", r[13], r[12] != 0, r[14] != 0);
    }

    /// <summary>AJ-series (SONiX 3151) : GET_FEATURE 05 → pourcentage ; null si trame de reconnexion ou &gt; 100.</summary>
    public static int? ParseAjSeries(byte[] r) => r.Length < 4 || r[0] != 0x05 || r[1] != 0 || r[2] != 0 || r[3] > 100 ? null : r[3];

    public static bool IsAj139Battery(byte[] r) => r.Length > 9 && r[0] == 0xAA && r[1] == 0x30;

    /// <summary>AJ139 : réponse → (%, en charge) ; null si &gt; 100.</summary>
    public static (int Percent, bool Charging)? ParseAj139(byte[] r) => !IsAj139Battery(r) || r[8] > 100 ? null : (r[8], r[9] != 0);

    /// <summary>Incott : réponse 89 → (%, en charge) ; null si périmée (octets 2 et 3 nuls), mauvais écho ou &gt; 100.</summary>
    public static (int Percent, bool Charging)? ParseIncott(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x09 || r[1] != 0x89 || (r[2] | r[3]) == 0) return null;
        int pct = r[2] & 0x7F;
        return pct > 100 ? null : (pct, (r[2] & 0x80) != 0);
    }

    /// <summary>Delux M800 : réponse à 0C 01 20 → (%, état, souris liée) ; null si invalide.</summary>
    public static (int Percent, byte State, bool Linked)? ParseDelux(byte[] r)
    {
        if (r.Length < 20 || r[0] != 0x0C || r[2] != 0x20 || r[18] > 100) return null;
        return (r[18], r[19], r[6] != 0);
    }

    public static byte[] BuildDeluxRequest(int featLen, byte seq) { var q = new byte[Math.Max(featLen, 33)]; q[0] = 0x0C; q[1] = 0x01; q[2] = 0x20; q[4] = seq; return q; }

    /// <summary>Wraith W1 : réponse 8F → modèle 18 / 20 ; -1 si une autre souris PixArt (Incott) ; 0 si ce n'est pas l'écho de 8F.</summary>
    public static int ParseW1Info(byte[] r) => r.Length < 4 || r[0] != 0x09 || r[1] != 0x8F ? 0 : (r[3] == 18 || r[3] == 20) ? r[3] : -1;

    /// <summary>Wraith W1 : rapport d'état 09 [09][batterie][…] → (%, en charge) ; null si &gt; 100.</summary>
    public static (int Percent, bool Charging)? ParseW1Status(byte[] r) => r.Length < 2 || r[0] != 0x09 || (r[1] & 0x7F) > 100 ? null : (r[1] & 0x7F, (r[1] & 0x80) != 0);

    // ================================================================== échanges

    static byte[]? ReadUntil(HidDev d, int timeoutMs, Func<byte[], bool> match)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) return null;
            if (match(r)) return r;
        }
        return null;
    }

    static void PollMotospeed(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0xFFE0, 0xFFF2, 0xFFF1 })
        {
            if (ctx.IsClaimed(0x0BDA, pid)) continue;
            foreach (var c in ctx.Find(0x0BDA, pid, c => c.Iface == 4 && c.OutLen >= 21))
            {
                var s = new DeviceStatus { Key = "motospeed:" + HidUtil.Hex4(pid), Name = "Motospeed X6", Kind = DeviceKind.Mouse };
                if (pid == 0xFFF1) { s.Via = "USB cable"; s.Charging = true; ctx.Add(s, 0x0BDA, pid); break; } // branchée : en charge, pas de lecture par le câble
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                if (!d.Write(0xB5, 0x06)) continue;
                var r = ReadUntil(d, 1500, x => x.Length > 20 && x[0] == 0xB4);
                if (r == null || ParseMotospeed(r) is not { } m) continue;
                s.Via = "2.4 GHz receiver"; s.Charging = m.Charging; s.Percent = m.Percent;
                ctx.Add(s, 0x0BDA, pid);
                break;
            }
        }
    }

    sealed class XentaListener
    {
        public HidDev? Dev; public byte[] Last = Array.Empty<byte>(), Prev = Array.Empty<byte>(); public long LastFrameAt;
        public int ArdorConfirmed = -1, ArdorCandidate = -1, ArdorCandidateCount; public long ArdorConfirmedAt;
    }
    static readonly Dictionary<string, XentaListener> Xenta = new();

    static void PollXenta(PollContext ctx)
    {
        var present = new HashSet<string>();
        foreach (ushort pid in new ushort[] { 0xFA60, 0xFA61, 0xFA65, 0xFA55, 0x2155 })
        {
            if (ctx.IsClaimed(0x1D57, pid)) continue;
            foreach (var c in ctx.Find(0x1D57, pid, c => c.Page == 0x000A && c.InLen >= 5))
            {
                present.Add(c.Path);
                if (!Xenta.TryGetValue(c.Path, out var l)) Xenta[c.Path] = l = new XentaListener();
                bool fresh = l.Dev == null;
                if (fresh)
                {
                    l.Dev = new HidDev();
                    if (!l.Dev.Open(c)) { l.Dev.Dispose(); Xenta.Remove(c.Path); continue; }
                }
                // On prend tout ce qui est en file depuis la dernière lecture ; un nouvel écouteur attend deux trames.
                long until = Environment.TickCount64 + (fresh ? 5000 : 0); int got = 0;
                while (true)
                {
                    long left = until - Environment.TickCount64;
                    if (fresh && got < 2 && left <= 0) break;
                    int wait = fresh && got < 2 ? (int)Math.Max(1, left) : 30;
                    var r = l.Dev!.Read(wait);
                    if (r == null || r.Length < 5) break;
                    if (ParseXenta(r) == null) continue;
                    l.Prev = l.Last; l.Last = r[..5]; l.LastFrameAt = Environment.TickCount64;
                    // Seule la forme de trame connue d'ARDOR sur FA60 utilise le décodeur de paliers ; on compare le pourcentage décodé, PAS les cinq octets.
                    if (pid == 0xFA60 && r[1] == 0x00 && ArdorFuryV2Percent(r) is var pct and >= 0)
                    {
                        if (pct == l.ArdorCandidate) { if (l.ArdorCandidateCount < 2) l.ArdorCandidateCount++; }
                        else { l.ArdorCandidate = pct; l.ArdorCandidateCount = 1; }
                        if (l.ArdorCandidateCount >= 2) { l.ArdorConfirmed = pct; l.ArdorConfirmedAt = l.LastFrameAt; }
                    }
                    got++;
                }
                // Pour ARDOR, une lecture établie reste utilisable entre deux lectures ; sans rapport depuis 45 s, on la marque endormie.
                bool ardorActive = pid == 0xFA60 && l.ArdorConfirmed >= 0 && Environment.TickCount64 - l.ArdorConfirmedAt <= 45000;
                if (l.Last.Length == 0) continue;
                if (!ardorActive && Environment.TickCount64 - l.LastFrameAt > 15000) continue; // autre Xenta / périmée
                if (ParseXenta(l.Last) is not { } frame) continue;
                bool cable = pid is 0xFA55 or 0x2155;
                int ardorPct = pid == 0xFA60 && !cable ? ArdorFuryV2Percent(l.Last) : -1;
                ctx.Add(XentaStatus(pid, frame, l.Prev.AsSpan().SequenceEqual(l.Last), ardorPct, ardorActive, l.ArdorConfirmed), 0x1D57, pid);
                break;
            }
        }
        foreach (var k in Xenta.Keys.Where(k => !present.Contains(k)).ToList()) { Xenta[k].Dev?.Dispose(); Xenta.Remove(k); } // récepteurs débranchés
    }

    static void PollAjazz(PollContext ctx)
    {
        foreach (ushort vendor in new ushort[] { 0x249A, 0x248A })
            foreach (var (vid, pid) in ctx.Products(vendor))
                foreach (var c in ctx.Find(vid, pid, c => c.Iface == 2 && c.OutLen == 33 && c.InLen == 33))
                {
                    using var d = new HidDev();
                    if (!d.Open(c)) continue;
                    d.Flush();
                    if (!d.Write(0x00, 0x10)) continue; // id de rapport 0, commande 10, charge nulle (somme 0)
                    var r = ReadUntil(d, 1500, x => ParseAj179(x) != null);
                    if (r == null || ParseAj179(r) is not { } a) continue;
                    bool receiver = vid == 0x249A;
                    var s = new DeviceStatus { Key = "ajazz:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = a.Name, Kind = DeviceKind.Mouse, Via = receiver ? "2.4 GHz receiver" : "USB cable", Charging = a.Charging };
                    s.Connected = !receiver || a.MouseConnected;
                    if (s.Connected) s.Percent = a.Percent;
                    ctx.Add(s, vid, pid);
                    break;
                }
    }

    static void PollAjazzAjSeries(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x3151))
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFFFF && c.Usage == 0x02 && c.FeatLen >= 65))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                var req = new byte[c.FeatLen]; req[0] = 0x00; req[1] = 0xF7; // rapport non numéroté ; sonde d'état : fait monter la liaison 2,4 GHz
                if (!d.SetFeature(req)) continue;
                Thread.Sleep(30);
                if (!d.GetFeature(0x05, out var r) || ParseAjSeries(r) is not { } pct) continue;
                var s = new DeviceStatus { Key = "ajazzaj:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = c.Product.Length > 0 ? c.Product : "AJAZZ mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Connected = pct > 0 };
                if (s.Connected) s.Percent = pct; // 0 = liaison établie mais souris éteinte
                ctx.Add(s, vid, pid);
                break;
            }
    }

    static void PollAjazzAj139(PollContext ctx)
    {
        foreach (ushort vendor in new ushort[] { 0xA8A5, 0xA8A4 })
        {
            const ushort pid = 0x2255;
            if (ctx.IsClaimed(vendor, pid)) continue;
            foreach (var c in ctx.Find(vendor, pid, c => c.Page == 0xFF01 && c.Usage == 0x0010 && c.OutLen >= 65 && c.InLen >= 65))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                byte[]? r = d.Write(0x00, 0x55, 0x30, 0xA5, 0x2E, 0x2E) ? ReadUntil(d, 700, IsAj139Battery) : null;
                r ??= d.Write(0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01) ? ReadUntil(d, 800, IsAj139Battery) : null;
                if (r == null || ParseAj139(r) is not { } b) continue;
                bool cable = vendor == 0xA8A4;
                var s = new DeviceStatus { Key = "ajazz139:" + HidUtil.Hex4(vendor) + ":" + HidUtil.Hex4(pid), Name = "AJAZZ wireless mouse", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver", Charging = b.Charging };
                s.Connected = cable || b.Percent > 0; // 0 du récepteur : souris éteinte ou hors de portée
                if (s.Connected) s.Percent = b.Percent;
                ctx.Add(s, vendor, pid);
                break;
            }
        }
    }

    // Wraith W1 : une fois qu'un récepteur a répondu comme W1, il le reste pour la suite de l'exécution : un W1 endormi n'est pas confié au pilote Incott.
    static readonly int[] KnownW1Model = { 0, 0 }; // 522C, 622C

    static void PollWraithW1(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x522C, 0x622C })
        {
            if (ctx.IsClaimed(0x093A, pid)) continue;
            int idx = pid == 0x622C ? 1 : 0;
            var colls = ctx.Find(0x093A, pid, c => c.Page == 0xFF05 && c.FeatLen >= 9);
            if (colls.Count == 0) continue;
            colls.Reverse(); // sur câble la première collection FF05 refuse SET_FEATURE
            using var d = new HidDev();
            int replied = 0; // code modèle de la lecture d'infos, -1 = a répondu mais pas un W1
            foreach (var c in colls)
            {
                if (!d.OpenFeatureOnly(c)) continue;
                if (!d.SetFeature(new byte[] { 0x09, 0x8F })) { d.Close(); continue; }
                for (int i = 0; i < 10 && replied == 0; i++)
                {
                    Thread.Sleep(50);
                    if (!d.GetFeature(0x09, out var r)) break;
                    replied = ParseW1Info(r);
                }
                if (replied != 0) break;
                d.Close();
            }
            if (replied < 0) { KnownW1Model[idx] = 0; continue; } // une autre souris PixArt (Incott) : on la laisse à PollIncott
            if (replied > 0) KnownW1Model[idx] = replied;
            int model = KnownW1Model[idx];
            if (model == 0) continue; // aucune réponse et jamais vue comme W1

            bool cable = pid == 0x622C;
            var s = new DeviceStatus { Key = "wraithw1:" + HidUtil.Hex4(pid), Name = model == 20 ? "Wraith W1 Freestyle" : "Wraith W1", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver", Connected = false };
            if (d.Ok)
            {
                // On écoute toutes les collections FF05 qui ont un rapport d'entrée : la poussée d'état peut arriver sur n'importe laquelle.
                var ins = new List<HidDev>();
                foreach (var c in ctx.Find(0x093A, pid, c => c.Page == 0xFF05 && c.InLen >= 2))
                {
                    var inn = new HidDev();
                    if (!inn.Open(c)) { inn.Dispose(); continue; }
                    inn.Flush(); ins.Add(inn);
                }
                (int Percent, bool Charging)? got = null;
                // Un 86 (lecture par bouton) comme l'aide _i() du pilote web : SET_FEATURE, 150 ms, GET_FEATURE, et un second GET 20 ms plus tard si la réponse est vide.
                bool ReadButton(byte button)
                {
                    if (!d.SetFeature(new byte[] { 0x09, 0x86, button })) return false;
                    for (int t = 0; t < 2; t++)
                    {
                        Thread.Sleep(t == 0 ? 150 : 20);
                        if (!d.GetFeature(0x09, out var f)) return true;
                        if (f.Length >= 4 && f[1] == 0x86) break;
                    }
                    return true;
                }
                void Listen(int ms)
                {
                    long end = Environment.TickCount64 + ms;
                    while (got == null && end - Environment.TickCount64 > 0)
                        foreach (var inn in ins) { var r = inn.Read(50); if (r != null && ParseW1Status(r) is { } st) { got = st; break; } }
                }
                try
                {
                    if (ins.Count > 0)
                    {
                        Thread.Sleep(200); // le W1 Service attend 200 ms après 8F avant sa requête suivante
                        // Balayage en lecture seule de 86 sur les boutons 0, 1, 2, 4, 3 (Ri() du pilote web), puis son battement de 700 ms (Bi()) pendant l'attente du rapport d'état.
                        bool sent = false;
                        foreach (byte b in new byte[] { 0, 1, 2, 4, 3 }) if (ReadButton(b)) sent = true;
                        if (sent)
                        {
                            Listen(400);
                            for (int beat = 0; beat < 4 && got == null; beat++) { if (!ReadButton(0)) break; Listen(550); }
                        }
                    }
                }
                finally { foreach (var inn in ins) inn.Dispose(); }
                if (got is { } g)
                {
                    s.Charging = g.Charging;
                    s.Connected = cable || g.Percent > 0 || g.Charging; // 0 du récepteur : souris éteinte
                    if (s.Connected) s.Percent = g.Percent;
                }
            }
            ctx.Add(s, 0x093A, pid); // pris même endormi, pour que PollIncott n'interroge jamais un W1
        }
    }

    static void PollIncott(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x522C, 0x622C })
        {
            if (ctx.IsClaimed(0x093A, pid)) continue;
            var colls = ctx.Find(0x093A, pid, c => c.Page == 0xFF05 && c.FeatLen >= 9); colls.Reverse();
            foreach (var c in colls)
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c) || !d.SetFeature(new byte[] { 0x09, 0x89 })) continue;
                (int Percent, bool Charging)? res = null;
                for (int i = 0; i < 10 && res == null; i++)
                {
                    Thread.Sleep(50);
                    if (!d.GetFeature(0x09, out var r)) break;
                    res = ParseIncott(r);
                }
                if (res is not { } p) continue;
                bool cable = pid == 0x622C;
                var s = new DeviceStatus { Key = "incott:" + HidUtil.Hex4(pid), Name = "Incott mouse", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver", Charging = p.Charging, Connected = cable || p.Percent > 0 }; // les identifiants sont partagés par le G23 V2 et le G24
                if (s.Connected) s.Percent = p.Percent;
                ctx.Add(s, 0x093A, pid);
                break;
            }
        }
    }

    static byte _deluxSeq;

    static void PollDeluxM800(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x5B2E, 0x5B2F })
        {
            if (ctx.IsClaimed(0x248A, pid)) continue;
            foreach (var c in ctx.Find(0x248A, pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen >= 33))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                bool receiver = pid == 0x5B2F;
                if (!d.SetFeature(BuildDeluxRequest(c.FeatLen, ++_deluxSeq))) continue;
                Thread.Sleep(20);
                if (!d.GetFeature(0x0C, out var r) || ParseDelux(r) == null) continue;
                var st = ParseDelux(r)!.Value;
                if (receiver && !st.Linked && d.SetFeature(BuildDeluxRequest(c.FeatLen, ++_deluxSeq))) // une seconde tentative si le récepteur ne voit pas encore la souris
                {
                    Thread.Sleep(20);
                    if (d.GetFeature(0x0C, out var r2) && ParseDelux(r2) is { } st2) st = st2;
                }
                var s = new DeviceStatus { Key = "delux:m800", Name = "Delux M800 Pro", Kind = DeviceKind.Mouse, Via = receiver ? "2.4 GHz receiver" : "USB cable", Connected = st.Linked || st.Percent > 0 };
                if (st.State == 2) { s.Percent = 100; s.Charging = true; }
                else if (st.State == 1) { s.Charging = true; if (s.Connected && st.Percent > 0) s.Percent = HidUtil.Clamp100(st.Percent); }
                else if (s.Connected && st.Percent > 0) s.Percent = HidUtil.Clamp100(st.Percent);
                if (!receiver && s.Connected) ctx.Claim(0x248A, 0x5B2F);
                ctx.Add(s, 0x248A, pid);
                break;
            }
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollMotospeed(ctx);
        PollXenta(ctx);
        PollDeluxM800(ctx);
        PollAjazz(ctx);
        PollAjazzAjSeries(ctx);
        PollAjazzAj139(ctx);
        PollWraithW1(ctx); // avant PollIncott : mêmes identifiants, commandes différentes
        PollIncott(ctx);
    }
}
