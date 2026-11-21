// Corsair : casques, souris et claviers sans fil (VID 1B1C).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/corsair.cpp, porté en C# — voir NOTICE.md.
//  * Famille Void / Void Pro / Void Elite / HS70 / HS80 : sortie C9 64 → [2] % (bit 7 = micro relevé), [4] état
//    (0 hors ligne, 1-2 correct, 4-5 en charge). D'après github.com/Sapd/HeadsetControl.
//  * Appareils « Bragi » (Virtuoso, Void V2, HS80 MAX, souris et claviers Slipstream) : octets 02 <cible> 02 <propriété> = GET ;
//    réponse [1] source, [2] 02, [3] état (0 = ok), [4..7] valeur petit-boutiste. Propriété 0F = batterie (0..1000), 10 = état de
//    charge (1 en charge, 3 chargé), 12 = identifiant produit (comme dans OpenRGB). Cible 08 = l'appareil ou le récepteur lui-même,
//    09+ = appareils appairés. GET est en lecture seule : l'appareil n'est jamais passé en mode logiciel.
//  * Souris Compx (ex. Corsair Sabre V2 Pro Ultralight Wireless, 1B1C:2B2A) : rapport de 17 octets, id 08, commande 03 = en ligne,
//    04 = batterie. Octet 6 = %, 7 = charge, 8-9 = millivolts, 16 = somme de contrôle. Collection FF02:0002.
//    Référence : documentation de protocole HeyOkay/HaloBattery.
using System.Text;

namespace BatteryTray.Providers.Hid;

public static class CorsairDriver
{
    const ushort Vid = 0x1B1C;

    static readonly HashSet<ushort> VoidPids = new()
    {
        0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56,
    };
    static readonly HashSet<ushort> BragiPids = new() { 0x2A08, 0x2A02, 0x0A97, 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x2B00 };

    static readonly Dictionary<ushort, string> CompxModels = new()
    {
        [0x2B2A] = "Corsair Sabre V2 Pro Ultralight",
        [0x2B34] = "Corsair Sabre v2 PRO WIRELESS MG", // demande d'appareil n° 2563, non testé
    };

    // ------------------------------------------------------------------ analyse des réponses (logique pure, testée)

    /// <summary>Famille Void : [2] niveau (bit 7 = micro), [4] état. Renvoie (connecté, en charge, niveau ou -1).</summary>
    public static (bool Connected, bool Charging, int Percent)? ParseVoid(byte[] r)
    {
        if (r.Length < 5) return null;
        byte st = r[4];
        bool charging = st is 4 or 5;
        int percent = st is 1 or 2 || charging ? HidUtil.Clamp100(r[2] & 0x7F) : -1;
        return (st != 0, charging, percent);
    }

    /// <summary>
    /// Valeur d'une réponse Bragi, ou null si la trame n'est pas la réponse attendue. « Plain » : réponse <c>source 02 état valeur</c> ;
    /// « Prefixed » : <c>02 source 02 état valeur</c>. Une notification ou la réponse d'une autre application est ignorée (null) ;
    /// un état non nul (ex. 5 = propriété inconnue) renvoie le code -1.
    /// </summary>
    public static long? ParseBragi(byte[] r, bool plain, byte target)
    {
        byte source = target == 0x08 ? (byte)0x00 : (byte)0x01;
        int o = plain ? 0 : 1; // position de l'octet « source » dans la réponse
        if (r.Length < o + 7) return null;
        if (r[o + 1] != 0x02 || r[o] != source) return null;
        if (r[o + 2] != 0x00) return -1;
        return r[o + 3] | (r[o + 4] << 8) | (r[o + 5] << 16) | ((long)r[o + 6] << 24);
    }

    /// <summary>Niveau Bragi (0..1000) converti en pourcentage.</summary>
    public static int BragiPercent(long level) => HidUtil.Clamp100((int)((level + 5) / 10));

    /// <summary>Somme de contrôle Compx : 0x55 moins la somme des 16 premiers octets.</summary>
    public static byte CompxChecksum(byte[] b)
    {
        int sum = 0;
        for (int i = 0; i < 16 && i < b.Length; i++) sum += b[i];
        return (byte)(0x55 - sum);
    }

    public static byte[] BuildCompxRequest(byte cmd)
    {
        var req = new byte[17];
        req[0] = 0x08; req[1] = cmd;
        req[16] = CompxChecksum(req);
        return req;
    }

    /// <summary>Réponse Compx valide : 17 octets, id 08, commande répétée, somme de contrôle correcte.</summary>
    public static bool IsValidCompxReply(byte[] r, byte cmd) => r.Length >= 17 && r[0] == 0x08 && r[1] == cmd && r[16] == CompxChecksum(r);

    sealed record Paired(string Name, DeviceKind Kind);
    static readonly Dictionary<long, Paired> PairedModels = new()
    {
        [0x1B7E] = new("Corsair Dark Core RGB Pro SE", DeviceKind.Mouse), // testé sur le récepteur Slipstream 1BDC
    };

    // ------------------------------------------------------------------ échanges avec l'appareil

    static DeviceStatus? PollVoid(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(0xC9, 0x64)) return null;
        var r = d.Read(1000);
        if (r == null || r.Length < 5) return null;
        var p = ParseVoid(r);
        if (p == null) return null;
        return new DeviceStatus
        {
            Key = "corsair:" + HidUtil.Hex4(c.Pid),
            Name = string.IsNullOrWhiteSpace(c.Product) ? "Corsair wireless headset" : c.Product,
            Kind = DeviceKind.Headset, Via = "2.4 GHz receiver",
            Connected = p.Value.Connected, Charging = p.Value.Charging, Percent = p.Value.Percent,
        };
    }

    sealed class Bragi : IDisposable
    {
        enum Mode { Unknown, Prefixed, Plain }
        readonly HidDev _d = new();
        readonly bool _numbered;
        Mode _mode = Mode.Unknown;
        public bool Ok { get; }

        public Bragi(HidColl c) { _numbered = c.OutLen == 64; Ok = _d.Open(c); }
        public void Dispose() => _d.Dispose();

        /// <summary>
        /// Deux cadrages existent. « Prefixed » (Bragi ancien) : 02 cible 02 propriété, réponse 02 source 02 état valeur.
        /// « Plain » (récepteurs Slipstream récents, ex. 1B1C:2B00, Scimitar Elite SE Wireless) : 00 cible 02 propriété (id 0),
        /// réponse source 02 état valeur. Le premier cadrage qui répond est mémorisé pour ce flux.
        /// </summary>
        public bool Get(byte target, byte prop, out long value, int timeoutMs = 300)
        {
            value = 0;
            if (_mode != Mode.Plain && GetFramed(false, target, prop, out value, timeoutMs)) { _mode = Mode.Prefixed; return true; }
            if (_mode == Mode.Prefixed) return false;
            if (GetFramed(true, target, prop, out value, timeoutMs)) { _mode = Mode.Plain; return true; }
            return false;
        }

        bool GetFramed(bool plain, byte target, byte prop, out long value, int timeoutMs)
        {
            value = 0;
            byte[] req = plain ? new byte[] { 0x00, target, 0x02, prop }
                : _numbered ? new byte[] { 0x02, target, 0x02, prop } : new byte[] { 0x00, 0x02, target, 0x02, prop };
            _d.Flush();
            if (!_d.Write(req)) return false;
            long end = Environment.TickCount64 + timeoutMs;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) return false;
                var r = _d.Read(left);
                if (r == null) return false;
                var parsed = ParseBragi(r, plain, target);
                if (parsed == null) continue; // notification ou réponse d'une autre application
                if (parsed < 0) return false;
                value = parsed.Value;
                return true;
            }
        }
    }

    static bool PollCompx(PollContext ctx, HidColl c, ushort vid, ushort pid, string model)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return false;
        bool answered = false, connected = false, asleep = false;
        int percent = -1; bool charging = false;

        byte[]? Query(byte cmd)
        {
            d.Flush();
            if (!d.Write(BuildCompxRequest(cmd))) return null;
            long end = Environment.TickCount64 + 500;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) return null;
                var r = d.Read(left);
                if (r == null || r.Length < 17) return null;
                if (IsValidCompxReply(r, cmd)) return r;
            }
        }

        // La souris est-elle connectée au récepteur ?
        var online = Query(0x03);
        if (online != null) { answered = true; connected = online[6] != 0; }
        // Commande 03 non prise en charge : on tente directement la batterie.
        var battery = !answered || connected ? Query(0x04) : null;
        if (battery != null)
        {
            answered = true; connected = true;
            if (battery[6] <= 100) percent = battery[6];
            charging = battery[7] != 0;
        }
        else if (answered && connected) asleep = true; // le récepteur la dit en ligne, mais la requête batterie a échoué
        if (!answered) return false;
        if (!connected) { ctx.Claim(vid, pid); return true; }
        ctx.Add(new DeviceStatus
        {
            Key = "corsair:" + HidUtil.Hex4(pid), Name = model, Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver",
            Percent = asleep ? -1 : percent, Charging = charging, Connected = !asleep,
        }, vid, pid);
        return true;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (VoidPids.Contains(pid))
            {
                foreach (var c in ctx.Find(vid, pid, c => (c.Page == 0xFFC5 || c.Page == 0xFF00) && c.OutLen > 0))
                {
                    var s = PollVoid(c);
                    if (s != null) { ctx.Add(s, vid, pid); break; }
                }
                continue;
            }
            if (CompxModels.TryGetValue(pid, out var model))
            {
                foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF02 && c.Usage == 0x0002 && c.OutLen >= 17 && c.InLen >= 17))
                    if (PollCompx(ctx, c, vid, pid, model)) break;
                continue;
            }

            bool knownBragi = BragiPids.Contains(pid);
            var colls = ctx.Find(vid, pid, c =>
            {
                bool sizes = (c.OutLen == 64 || c.OutLen == 65) && (c.InLen == 64 || c.InLen == 65);
                return sizes && (c.Page == 0xFF42 || (knownBragi && c.Page >= 0xFF00) || (knownBragi && c.Iface == 4));
            });
            foreach (var c in colls)
            {
                using var b = new Bragi(c);
                if (!b.Ok) continue;
                bool any = false;
                string product = c.Product;
                // Appareils appairés derrière un récepteur d'abord, puis l'appareil lui-même (filaire / Bluetooth).
                foreach (byte target in new byte[] { 0x09, 0x0A, 0x0B, 0x08 })
                {
                    if (target == 0x08 && any) break;
                    long level = 0;
                    bool ok = b.Get(target, 0x0F, out level);
                    for (int retry = 0; ok && (level == 0 || level > 1000) && retry < 2; retry++) ok = b.Get(target, 0x0F, out level);
                    if (!ok || level > 1000) continue;

                    var s = new DeviceStatus
                    {
                        Key = $"corsair:{HidUtil.Hex4(pid)}:{target}",
                        Name = string.IsNullOrWhiteSpace(product) ? "Corsair wireless device" : product,
                    };
                    s.Kind = DeviceNames.Guess(s.Name);
                    // Un récepteur porte son propre nom, pas celui de l'appareil derrière lui. La propriété 12 est l'identifiant
                    // produit de l'appareil appairé (lecture seule) ; les casques Corsair utilisent 0Axx.
                    if (target != 0x08 && b.Get(target, 0x12, out long devPid))
                    {
                        if (PairedModels.TryGetValue(devPid, out var paired)) { s.Name = paired.Name; s.Kind = paired.Kind; }
                        else if ((devPid >> 8) == 0x0A)
                        {
                            s.Kind = DeviceKind.Headset;
                            if (product.Contains("Receiver", StringComparison.Ordinal)) s.Name = "Corsair wireless headset";
                        }
                    }
                    // Casques et récepteurs de casques : identifiants 0Axx (le récepteur du HS80 RGB, 0A6B, se nomme « ... Wireless Gaming Receiver »
                    // et ses collections ressemblent à un clavier).
                    if (s.Kind == DeviceKind.Other && (pid >> 8) == 0x0A) s.Kind = DeviceKind.Headset;
                    if (s.Kind == DeviceKind.Other) s.Kind = HidUtil.KindFromCollections(ctx.Colls, vid, pid);
                    s.Via = target == 0x08 ? "USB / Bluetooth" : "Corsair receiver";
                    s.Percent = BragiPercent(level);
                    if (b.Get(target, 0x10, out long charge)) s.Charging = charge == 1 || charge == 3;
                    ctx.Out.Add(s);
                    any = true;
                }
                if (any) { ctx.Claim(vid, pid); break; }
            }
        }
    }
}
