// Claviers sans fil AULA F75 / F87 Pro / F99, MCHOSE (K99 V2, G75 Pro, X75…) et Redragon Horus Pro (K618RGB-PRO) sur les récepteurs 2,4 GHz « BY » : 3554:FA09 (Compx, partagé par ces marques) et 41E4:2001 de MCHOSE ;
// AULA HERO 68 Air sur le « 8K Wireless Receiver » (372E:106C) ; souris AULA SC580 / SC580X (câble 088D:062E, récepteur 089D:062F).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/aula.cpp, porté en C# — voir NOTICE.md.
// Les récepteurs MCHOSE K87 / K99 existent aussi en 05AC:024F (identifiant d'Apple), celui des récepteurs SONiX de SonixDriver. M HUB liste 1452/591 : les MCHOSE se distinguent par leur disposition : la collection BY
// sur l'interface 1 (FF02:0002, rapports de 20 octets) à côté d'une collection de fonctionnalité FF04:0002, là où les récepteurs SONiX n'ont que FF60:0061 sur l'interface 3. Seule cette disposition exacte est sondée sur cet identifiant.
// Le récepteur expose une collection vendeur de 20 octets (page FF02 ou FF04, usage 02). La batterie est un rapport de sortie 0x13 de 20 octets portant la commande 0x4A et une somme finale. La réponse est une autre trame 0x13
// dont l'octet de commande, bit haut masqué, vaut 0x4A ; sa charge commence à l'octet 5 : le niveau (1..100), puis l'octet d'état (quartet bas : plein, quartet haut : en charge ; AULA donne 0x01 sur batterie et 0x10 câble branché).
// Le récepteur pousse aussi d'autres trames 0x13 (dont un état 0x0A) : l'identifiant seul n'identifie pas la réponse. Le micrologiciel MCHOSE laisse la fin de la réponse à 0 : une telle réponse n'est acceptée qu'en trame unique.
// Un clavier qui somnole laisse la première requête sans réponse : on renvoie toutes les ~1,3 s dans une échéance de 4 s. L'octet 2 alterne 00 (PyFlat, sanshoku) et 01 (Womier L65) ; la réponse est reconnue par commande et somme.
// Les récepteurs « 2.4G Wireless Receiver » / « MCHOSE 2.4G Wireless » sont d'abord interrogés pour leur code de modèle de six octets (commande 0x05, lecture seule), comme M HUB. Sources : PyFlat/Device-Battery-Info (PR 6),
// deepan-alve/womier-l65-linux, ushineko/sanshoku issue 36, pilote web officiel M HUB (mchose.com.cn), wushuai0802/myfinder-device-battery.
// HERO 68 Air : protocole acquitté sur la page FF60 / usage 61 (interface 2) : rapports de 20 octets 09 66 + trois octets de contrôle + 14 de charge + somme ; octet 3 (7 bits bas) = indice de trame 1..5, octet 4 (7 bits bas) = type
// (0E données, 00 ACK, 08 fin de l'hôte, 07 fin du clavier) ; les bits hauts des octets 2, 3 et 4 forment un compteur de 3 bits. Un message = 4 trames de données (56 octets) + une fin ; chaque trame est acquittée.
// Requête 09 87 00 00 01 00 ; réponse 87 00 00 01 00 02 <niveau> <drapeaux> (0x10 en charge). Capture USB d'AULA Hub (issue #201). SC580 : collection FF02 / usage 0000, rapports de 33 octets : commande 0x30 (GetDeviceInfo),
// état 0 ok, 1 occupé, 2 souris hors ligne, 3 erreur ; données (dès l'octet 4) : « M137 », micrologiciel, capteur, état de charge (0 batterie, 1 en charge, 2 plein), niveau 0..100, en ligne. Source : Crucialjun/omarchy-aula-SC580 (MIT).
namespace BatteryTray.Providers.Hid;

public static class AulaDriver
{
    public sealed record Receiver(ushort Vid, ushort Pid);
    public static readonly Receiver[] Receivers = { new(0x3554, 0xFA09), new(0x3554, 0xFB40), new(0x41E4, 0x2001), new(0x05AC, 0x024F) };

    public static bool IsAppleId(ushort vid, ushort pid) => vid == 0x05AC && pid == 0x024F;

    public static bool IsByCollection(HidColl c) =>
        !IsAppleId(c.Vid, c.Pid) ? (c.Page == 0xFF02 || c.Page == 0xFF04) && c.Usage == 0x0002 && c.OutLen >= 20 && c.InLen >= 20
                                 : c.Iface == 1 && c.Page == 0xFF02 && c.Usage == 0x0002 && c.InLen == 20 && c.OutLen == 20;

    public static bool HasMchoseLayout(PollContext ctx, ushort vid, ushort pid) =>
        ctx.Find(vid, pid, c => c.Iface == 1 && c.Page == 0xFF04 && c.Usage == 0x0002 && c.FeatLen > 0 && c.InLen == 0 && c.OutLen == 0).Count > 0;

    // Table keyboardPwds de M HUB : le code de modèle de six octets renvoyé par la commande 0x05.
    static readonly (byte[] Code, string Name)[] ModelTable =
    {
        (new byte[] { 3, 0, 0, 0, 2, 0x1C }, "MCHOSE G87"), (new byte[] { 3, 0, 0, 0, 0, 0x77 }, "MCHOSE K99"), (new byte[] { 3, 0, 0, 0, 1, 0xDF }, "MCHOSE K99 V2"), (new byte[] { 3, 0, 0, 0, 1, 0x32 }, "MCHOSE G98"),
        (new byte[] { 3, 0, 0, 0, 0, 0xD6 }, "MCHOSE X75"), (new byte[] { 0x0A, 0, 0, 0, 0, 3 }, "MCHOSE G98 Pro"), (new byte[] { 3, 0, 0, 0, 1, 0xC6 }, "MCHOSE X75 V2"), (new byte[] { 3, 0, 0, 0, 1, 0x15 }, "MCHOSE G75 Pro"),
        (new byte[] { 0x0A, 0, 0, 0, 0, 0x18 }, "MCHOSE G98 V2"), (new byte[] { 0x0A, 0, 0, 0, 0, 0x2B }, "MCHOSE UT98"), (new byte[] { 0x0A, 0, 0, 0, 0, 1 }, "MCHOSE Z75"), (new byte[] { 0x0A, 0, 0, 0, 0, 9 }, "MCHOSE Z75S"),
        (new byte[] { 3, 0, 0, 0, 1, 0xC7 }, "MCHOSE KX75"), (new byte[] { 3, 0, 0, 0, 1, 0x74 }, "MCHOSE GX87"), (new byte[] { 3, 0, 0, 0, 1, 0xED }, "MCHOSE GX87 V2"), (new byte[] { 3, 0, 0, 0, 1, 0x35 }, "MCHOSE K87"),
        (new byte[] { 3, 0, 0, 0, 3, 0x35 }, "MCHOSE G75 V2"), (new byte[] { 0x0A, 0, 0, 0, 0, 0x34 }, "MCHOSE G87 V2"), (new byte[] { 3, 0, 0, 0, 0, 0x6A }, "Redragon Horus Pro"), (new byte[] { 3, 0, 0, 0, 3, 0xDA }, "Redragon Horus Pro V2"),
    };

    public static byte Sum(byte[] b, int n) { int s = 0; for (int i = 0; i < n; i++) s += b[i]; return (byte)(s & 0xFF); }

    public static byte[] BuildRequest(byte cmd, byte packets) { var r = new byte[20]; r[0] = 0x13; r[1] = cmd; r[2] = packets; r[19] = Sum(r, 19); return r; }

    /// <summary>Vrai si <paramref name="r"/> est la réponse à la commande (octet de commande bit haut masqué, charge non nulle, somme exacte ou trame unique MCHOSE à fin nulle).</summary>
    public static bool IsReply(byte[] r, byte cmd)
    {
        if (r.Length < 20 || r[0] != 0x13 || (r[1] & 0x7F) != cmd) return false;
        if (r.AsSpan(4, 15).ToArray().All(b => b == 0)) return false; // écho d'une requête
        bool checksum = r[19] == Sum(r, 19);
        bool mchose = r[19] == 0 && r[2] == 1 && r[3] == 0; // MCHOSE laisse la fin à 0
        return checksum || mchose;
    }

    /// <summary>Nom du modèle d'après le code (octets 5..10 d'une réponse 0x05, ou 4..9 dans une capture d'utilisateur), null si inconnu.</summary>
    public static string? IdentifyModel(byte[] r)
    {
        foreach (int off in new[] { 5, 4 })
            foreach (var (code, name) in ModelTable)
                if (r.Length >= off + 6 && r.AsSpan(off, 6).SequenceEqual(code)) return name;
        return null;
    }

    /// <summary>Réponse batterie 0x4A : (niveau, en charge) ; null si niveau hors 1..100.</summary>
    public static (int Level, bool Charging)? ParseBattery(byte[] r) => r.Length < 7 || r[5] is < 1 or > 100 ? null : (r[5], (r[6] >> 4) == 1);

    // Une échéance absolue unique ; renvoie la requête toutes les 1,3 s.
    static byte[]? Exchange(HidDev d, byte cmd, int totalMs)
    {
        long start = Environment.TickCount64, end = start + totalMs; const int resendMs = 1300; int sent = 0;
        d.Flush();
        for (; ; )
        {
            long now = Environment.TickCount64;
            if (now >= end) return null;
            if (now >= start + sent * (long)resendMs) { if (!d.Write(BuildRequest(cmd, (byte)(sent & 1)))) return null; sent++; }
            long nextSend = start + sent * (long)resendMs;
            int wait = (int)Math.Max(1, Math.Min(nextSend, end) - now);
            long before = Environment.TickCount64;
            var r = d.Read(wait);
            // Une lecture qui échoue aussitôt est une erreur d'E/S (récepteur débranché), pas un délai : on s'arrête.
            if (r == null) { if ((int)(Environment.TickCount64 - before) + 20 < wait) return null; continue; }
            if (r.Length < 20) continue;
            if (IsReply(r, cmd)) return r;
        }
    }

    static string? IdentifyModel(HidDev d) => Exchange(d, 0x05, 1300) is { } r ? IdentifyModel(r) : null;

    // ---------------------------------------------------------------- HERO 68 Air

    const byte HeroId = 0x09, HeroMagic = 0x66, HeroData = 0x0E, HeroAck = 0x00, HeroTrailerHost = 0x08, HeroTrailerDev = 0x07;
    const int HeroDeadlineMs = 1000, HeroResendMs = 150;

    public static byte SumRange(byte[] b, int from, int to) { int s = 0; for (int i = from; i < to; i++) s += b[i]; return (byte)(s & 0xFF); }

    public static byte[] HeroHeader(int counter, byte index, byte type)
    {
        var f = new byte[20]; f[0] = HeroId; f[1] = HeroMagic;
        f[2] = (byte)(0x05 | ((counter & 4) != 0 ? 0x80 : 0)); f[3] = (byte)(index | ((counter & 2) != 0 ? 0x80 : 0)); f[4] = (byte)(type | ((counter & 1) != 0 ? 0x80 : 0));
        return f;
    }

    public static byte[] HeroDataFrame(int counter, byte index, byte[] msg)
    {
        var f = HeroHeader(counter, index, HeroData);
        for (int i = 0; i < 14; i++) f[5 + i] = msg[(index - 1) * 14 + i];
        f[19] = SumRange(f, 1, 19);
        return f;
    }

    public static byte[] HeroTrailer(int counter, byte[] msg)
    {
        var f = HeroHeader(counter, 5, HeroTrailerHost);
        f[12] = (byte)(0xFF - SumRange(msg, 0, msg.Length)); f[13] = SumRange(f, 1, 13);
        return f;
    }

    public static byte[] HeroAckFrame(byte[] r)
    {
        var f = new byte[20]; f[0] = HeroId; f[1] = HeroMagic; f[2] = r[2]; f[3] = r[3]; f[4] = (byte)(r[4] & 0x80); f[5] = SumRange(f, 1, 5);
        return f;
    }

    public static bool IsAckFor(byte[] r, byte[] f) => (r[4] & 0x7F) == HeroAck && r[2] == f[2] && r[3] == f[3] && (r[4] & 0x80) == (f[4] & 0x80) && r[5] == SumRange(r, 1, 5);

    sealed class HeroLink
    {
        readonly HidDev _d; readonly long _end = Environment.TickCount64 + HeroDeadlineMs;
        int _level = -1, _flags = -1; bool _inReply, _replyDone, _ioError;
        public HeroLink(HidDev d) => _d = d;
        public int Level => _level;
        public bool Charging => _flags >= 0 && (_flags & 0x10) != 0;
        int Remaining() => (int)Math.Max(0, _end - Environment.TickCount64);

        public bool SendMessage(byte[] msg)
        {
            int counter = 1; // AULA Hub commence une session à 1 aussi
            for (byte i = 1; i <= 4; i++, counter = (counter + 1) & 7) if (!SendFrame(HeroDataFrame(counter, i, msg))) return false;
            return SendFrame(HeroTrailer(counter, msg));
        }

        public bool WaitForBattery()
        {
            while (!(_level >= 0 && _replyDone)) if (ReadFrame(Remaining()) == null) break;
            return _level >= 0;
        }

        bool SendFrame(byte[] f)
        {
            while (Remaining() > 0)
            {
                if (!_d.Write(f)) return false;
                long resendAt = Environment.TickCount64 + HeroResendMs;
                for (; ; )
                {
                    long wait = Math.Min(resendAt - Environment.TickCount64, Remaining());
                    if (wait <= 0) break;
                    var r = ReadFrame((int)wait);
                    if (r == null) { if (_ioError) return false; break; }
                    if (IsAckFor(r, f)) return true;
                }
            }
            return false;
        }

        byte[]? ReadFrame(int waitMs)
        {
            for (; ; )
            {
                if (waitMs <= 0) return null;
                long before = Environment.TickCount64;
                var r = _d.Read(waitMs);
                int took = (int)(Environment.TickCount64 - before);
                if (r == null) { if (took + 20 < waitMs) _ioError = true; return null; }
                waitMs -= took;
                if (r.Length < 20 || r[0] != HeroId || r[1] != HeroMagic) continue; // rapport sans rapport
                byte type = (byte)(r[4] & 0x7F);
                if (type == HeroData)
                {
                    if (r[19] != SumRange(r, 1, 19)) continue; // abîmée : pas d'ACK, le clavier renvoie
                    _d.Write(HeroAckFrame(r));
                    if ((r[3] & 0x7F) == 1) Inspect(r);
                }
                else if (type == HeroTrailerDev) { _d.Write(HeroAckFrame(r)); if (_inReply) _replyDone = true; _inReply = false; }
                return r;
            }
        }

        void Inspect(byte[] r)
        {
            _inReply = false;
            if (r[5] == 0x87 && r[11] is >= 1 and <= 100) { _level = r[11]; _flags = r[12]; _inReply = true; } // réponse à notre requête
            else if (r[5] == 0xFE && r[6] == 0x05 && r[7] is >= 1 and <= 100 && _level < 0) { _level = r[7]; _flags = r[8]; } // poussé par le clavier lui-même ; on attend la vraie réponse
        }
    }

    static (int Level, bool Charging)? QueryHeroBattery(HidDev d)
    {
        var msg = new byte[56]; msg[0] = 0x09; msg[1] = 0x87; msg[4] = 0x01;
        d.Flush();
        var link = new HeroLink(d);
        if (!link.SendMessage(msg) || !link.WaitForBattery()) return null;
        return (link.Level, link.Charging);
    }

    // D'autres claviers partagent ces identifiants de récepteur : le Redragon K673 vient aussi en 3554:FA09, ignore ces trames, et chaque requête le réveille juste après qu'il s'est endormi (issue #191). Un récepteur qui
    // n'a jamais répondu depuis son branchement est donc interrogé de moins en moins souvent une fois silencieux un moment (5, 10, 20, 40 minutes, puis toutes les heures). Une réponse rétablit la requête normale ; débrancher recommence.
    sealed class ReceiverState { public bool Answered; public int Misses, Backoffs; public long FirstMiss, NextTry; }
    const int MissesBeforeBackoff = 3;
    const long BackoffAfterMs = 5 * 60 * 1000, FirstBackoffMs = 5 * 60 * 1000, MaxBackoffMs = 60 * 60 * 1000;
    static readonly ReceiverState[] States = Receivers.Select(_ => new ReceiverState()).ToArray();

    // ---------------------------------------------------------------- SC580

    const byte ScGetInfo = 0x30, ScOk = 0, ScBusy = 1, ScOffline = 2;
    const int ScDeadlineMs = 1000, ScResendMs = 250;
    public enum ScResult { Ok, Offline, Failed }
    static readonly (ushort Vid, ushort Pid, bool Cable)[] Sc580 = { (0x088D, 0x062E, true), (0x089D, 0x062F, false) };

    public static byte[] BuildScRequest() { var r = new byte[33]; r[1] = ScGetInfo; r[3] = 0x01; return r; } // identifiant 0 + 32 octets ; la somme d'une charge vide vaut 0

    /// <summary>Données SC580 d'une réponse OK (octets 4..30) ; la somme (octet 31) porte sur les octets 4..30.</summary>
    public static byte[]? ParseScInfo(byte[] r) => r.Length < 32 || r[0] != ScGetInfo || r[1] != ScOk || r[31] != SumRange(r, 4, 31) ? null : r[4..31];

    /// <summary>Données de l'info SC580 → (hors ligne, % ou -1, en charge, niveau invalide).</summary>
    public static (bool Offline, int Percent, bool Charging, bool Invalid) DecodeSc(ScResult res, byte[] data)
    {
        bool offline = res == ScResult.Offline || data[10] == 0;
        if (!offline && data[9] > 100) return (false, -1, false, true);
        if (offline) return (true, -1, false, false);
        if (data[8] == 1) return (false, -1, true, false); // en charge la souris annonce un 100 fixe : niveau inconnu, on garde le dernier réel avec le symbole de charge
        return (false, data[9], false, false);
    }

    static (ScResult, byte[])? Sc580Info(HidDev d)
    {
        var req = BuildScRequest();
        long end = Environment.TickCount64 + ScDeadlineMs;
        int Left() => (int)Math.Max(0, end - Environment.TickCount64);
        d.Flush();
        while (Left() > 0)
        {
            if (!d.Write(req)) return null;
            long resendAt = Environment.TickCount64 + ScResendMs; bool resend = false;
            while (!resend)
            {
                int wait = (int)Math.Min(resendAt - Environment.TickCount64, Left());
                if (wait <= 0) break;
                long before = Environment.TickCount64;
                var r = d.Read(wait);
                if (r == null) { if ((int)(Environment.TickCount64 - before) + 20 < wait) return null; continue; }
                if (r.Length < 32) continue;
                if (r[0] == 0xD1 || r[0] == 0xD2) continue; // notifications poussées d'énergie / DPI
                if (r[0] != ScGetInfo) { resend = true; continue; } // la réponse du récepteur à une requête précédente
                switch (r[1])
                {
                    case ScOk: if (ParseScInfo(r) is { } data) return (ScResult.Ok, data); break;
                    case ScOffline: return (ScResult.Offline, new byte[27]);
                    case ScBusy: Thread.Sleep(Math.Min(300, Left())); resend = true; break;
                    default: resend = true; break;
                }
            }
        }
        return null;
    }

    static void PollSc580(PollContext ctx)
    {
        const string key = "aula:sc580"; // une seule entrée quand la souris charge sur le câble à côté de son récepteur
        foreach (var (vid, pid, cable) in Sc580)
        {
            if (ctx.IsClaimed(vid, pid)) continue;
            if (ctx.Out.Any(o => o.Key == key)) { ctx.Claim(vid, pid); continue; } // le câble a répondu le premier : on laisse le récepteur
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF02 && c.Usage == 0x0000 && c.OutLen >= 33 && c.InLen >= 33))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                if (Sc580Info(d) is not { } res) continue;
                ctx.Claim(vid, pid);
                // Le récepteur répond état 2 (et pousse D1 00) dès que la souris somnole (vu 20-60 s après le dernier mouvement) ou est éteinte, et état 0 dès qu'elle se réveille : cela est signalé comme une entrée
                // « endormie » pour que la zone de notification garde le dernier niveau (l'omettre faisait disparaître la souris une fois le délai de grâce écoulé).
                var dec = DecodeSc(res.Item1, res.Item2);
                if (dec.Invalid) break;
                var s = new DeviceStatus { Key = key, Name = "AULA SC580", Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver", Vid = vid };
                if (dec.Offline) s.Connected = false; else if (dec.Charging) s.Charging = true; else s.Percent = dec.Percent;
                ctx.Out.Add(s);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- Poll

    public static void Poll(PollContext ctx)
    {
        PollSc580(ctx);

        for (int i = 0; i < Receivers.Length; i++)
        {
            ushort vid = Receivers[i].Vid, pid = Receivers[i].Pid;
            var st = States[i];
            if (ctx.Find(vid, pid).Count == 0) { st.Answered = false; st.Misses = st.Backoffs = 0; st.FirstMiss = st.NextTry = 0; } // débranché
            if (ctx.IsClaimed(vid, pid)) continue;
            if (!st.Answered && st.NextTry != 0 && Environment.TickCount64 < st.NextTry) continue;
            bool tried = false, answered = false;
            if (IsAppleId(vid, pid) && !HasMchoseLayout(ctx, vid, pid)) continue; // un récepteur SONiX : SonixDriver
            foreach (var c in ctx.Find(vid, pid, IsByCollection))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                // Les noms génériques de récepteur portent un code de modèle (M HUB n'interroge aucun autre appareil 3554 / 41E4).
                string product = c.Product;
                bool generic = product.Contains("2.4G Wireless Receiver", StringComparison.Ordinal) || product.Contains("MCHOSE 2.4G Wireless", StringComparison.Ordinal);
                // M HUB nomme un clavier 05AC:024F d'après sa chaîne produit (« K87 », « K99 ») : on demande donc le code de modèle là, quel que soit le nom.
                string? model = generic || IsAppleId(vid, pid) ? IdentifyModel(d) : null;
                bool mchose = vid == 0x41E4 || IsAppleId(vid, pid) || model != null;
                tried = true;
                // L'octet 2 alterne 00 / 01 au renvoi ; M HUB envoie 01, les références AULA 00.
                if (Exchange(d, 0x4A, 4000) is not { } r || ParseBattery(r) is not { } b) continue;
                answered = true;
                string name = model ?? (IsAppleId(vid, pid) && !generic && product.Length > 0 ? product : mchose ? "MCHOSE wireless keyboard" : "AULA wireless keyboard");
                // Un clavier inactif manque parfois l'échéance de 4 s (4 lectures sur 15 sur un F75 éveillé) : l'interface garde la dernière valeur.
                ctx.Add(new DeviceStatus { Key = "aula:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Percent = b.Level, Charging = b.Charging }, vid, pid);
                break;
            }
            if (answered) st.Answered = true;
            else if (tried && !st.Answered)
            {
                long now = Environment.TickCount64;
                if (st.Misses++ == 0) st.FirstMiss = now;
                if (st.Misses >= MissesBeforeBackoff && now - st.FirstMiss >= BackoffAfterMs)
                {
                    int doublings = Math.Min(st.Backoffs++, 4);
                    st.NextTry = now + Math.Min(FirstBackoffMs << doublings, MaxBackoffMs);
                }
            }
        }

        foreach (var (vid, pid) in ctx.Products(0x372E))
        {
            if (pid != 0x106C) continue; // HERO 68 Air « 8K Wireless Receiver » ; les autres produits 372E sont inconnus
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF60 && c.Usage == 0x0061 && c.OutLen >= 20 && c.InLen >= 20))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                if (QueryHeroBattery(d) is not { } b) continue;
                ctx.Add(new DeviceStatus { Key = "aula:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "AULA wireless keyboard", Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Percent = b.Level, Charging = b.Charging }, vid, pid);
                break;
            }
        }
    }
}
