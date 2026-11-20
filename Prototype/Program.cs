// Prototype BatteryTray — étape 3 : détection + lecture de batterie dans la console.
//
// Règles :
//  - Lecture seule : on n'envoie que des REQUÊTES de lecture (HID++ GetBattery, Razer get_battery).
//    Aucune écriture de réglage sur les appareils.
//  - Aucun pourcentage inventé : une valeur n'est affichée que si l'appareil l'a renvoyée.
//    Sinon -> "Niveau indisponible".
//  - Aucun réseau.
using System.Text;
using HidSharp;

Console.OutputEncoding = Encoding.UTF8;
bool verbose = args.Contains("-v");

Console.WriteLine("=== BatteryTray prototype ===\n");
var all = DeviceList.Local.GetHidDevices().ToList();

// Mode casque PRO X 2 : requête batterie sur le rapport 0x51 (page FFA0). Protocole décrit dans un patch
// noyau Linux citant HeadsetControl (communauté, non documenté par Logitech) — à valider contre G HUB.
if (args.Contains("-battery"))
{
    var d = all.FirstOrDefault(x => x.VendorID == 0x046D && x.ProductID == 0x0AF7 && x.DevicePath.Contains("col03"));
    if (d == null || !d.TryOpen(out HidStream st)) { Console.WriteLine("Interface FFA0 indisponible"); return; }
    using (st)
    {
        st.ReadTimeout = 500;
        var w = new byte[64];
        new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }.CopyTo(w, 0);
        st.Write(w);
        var b = new byte[d.GetMaxInputReportLength()];
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end)
        {
            int n;
            try { n = st.Read(b, 0, b.Length); } catch (TimeoutException) { continue; } catch { break; }
            Console.WriteLine($"<- {Convert.ToHexString(b, 0, Math.Min(n, 16))}");
            if (n >= 13 && b[0] == 0x51 && b[1] == 0x0B && b[8] == 0x04 && b[10] <= 100)
                Console.WriteLine($"=> batterie {b[10]} %, état brut 0x{b[12]:X2} ({(b[12] == 0x02 ? "en charge" : "pas en charge")})");
        }
    }
    return;
}

// Mode sonde casque : requêtes HID++ (ping IRoot, lecture uniquement) avec les ID de rapport du PRO X 2
if (args.Contains("-probe"))
{
    foreach (var (path, id, outLen) in new[] { ("col03", (byte)0x51, 64), ("col02", (byte)0x06, 62) })
    {
        var d = all.FirstOrDefault(x => x.VendorID == 0x046D && x.ProductID == 0x0AF7 && x.DevicePath.Contains(path));
        if (d == null || !d.TryOpen(out HidStream st)) { Console.WriteLine($"{path}: indisponible"); continue; }
        using (st)
        {
            st.ReadTimeout = 700;
            foreach (byte idx in new byte[] { 0xFF, 0x00, 0x01 })
            {
                var w = new byte[outLen]; w[0] = id; w[1] = idx; w[2] = 0x00; w[3] = 0x1A; w[6] = 0x0A; // IRoot.Ping (HID++ 2.0)
                try { st.Write(w); } catch (Exception e) { Console.WriteLine($"{path} idx {idx:X2}: écriture impossible ({e.Message})"); continue; }
                var b = new byte[d.GetMaxInputReportLength()];
                try { int n = st.Read(b, 0, b.Length); Console.WriteLine($"{path} id {id:X2} idx {idx:X2} <- {Convert.ToHexString(b, 0, Math.Min(n, 24))}"); }
                catch { Console.WriteLine($"{path} id {id:X2} idx {idx:X2}: pas de réponse"); }
            }
        }
    }
    return;
}

// Mode diagnostic : dump des descripteurs + écoute passive (aucune écriture)
if (args.Contains("-dump"))
{
    foreach (var d in all.Where(d => d.VendorID == 0x046D && d.ProductID == 0x0AF7))
    {
        Console.WriteLine($"\n## {d.DevicePath}");
        try
        {
            var raw = d.GetRawReportDescriptor();
            Console.WriteLine("descripteur: " + Convert.ToHexString(raw));
            var desc = d.GetReportDescriptor();
            foreach (var r in desc.InputReports) Console.WriteLine($"  IN  id={r.ReportID:X2} len={r.Length}");
            foreach (var r in desc.OutputReports) Console.WriteLine($"  OUT id={r.ReportID:X2} len={r.Length}");
        }
        catch (Exception e) { Console.WriteLine("  " + e.Message); }
        if (d.TryOpen(out HidStream st))
        {
            using (st)
            {
                st.ReadTimeout = 4000;
                var b = new byte[d.GetMaxInputReportLength()];
                Console.WriteLine("  écoute passive 4 s...");
                try { int n = st.Read(b, 0, b.Length); Console.WriteLine("  <- " + Convert.ToHexString(b, 0, n)); }
                catch (TimeoutException) { Console.WriteLine("  (rien reçu)"); }
                catch (Exception e) { Console.WriteLine("  " + e.Message); }
            }
        }
        else Console.WriteLine("  ouverture refusée");
    }
    return;
}

// ---------- 1. Inventaire des interfaces HID des marques ciblées ----------
Console.WriteLine("--- Interfaces HID (Logitech 046D, Razer 1532) ---");
foreach (var d in all.Where(d => d.VendorID is 0x046D or 0x1532).OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ThenBy(d => d.DevicePath))
{
    Console.WriteLine($"{d.VendorID:X4}:{d.ProductID:X4}  {Safe(() => d.GetProductName(), "?"),-28} {Describe(d)}");
    if (verbose) Console.WriteLine("     " + d.DevicePath);
}

// ---------- 2. Interfaces exposant la "Battery Strength" HID standard (page 0x85) ----------
Console.WriteLine("\n--- Interfaces HID avec Usage Page 0x85 (Battery System) ---");
int stdCount = 0;
foreach (var d in all)
{
    try
    {
        var desc = d.GetReportDescriptor();
        bool has85 = desc.DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) == 0x85));
        if (has85) { stdCount++; Console.WriteLine($"{d.VendorID:X4}:{d.ProductID:X4}  {Safe(() => d.GetProductName(), "?")}"); }
    }
    catch { /* descripteur illisible : on ignore */ }
}
if (stdCount == 0) Console.WriteLine("(aucune)");

// ---------- 3. Logitech HID++ 2.0 ----------
Console.WriteLine("\n--- Logitech (HID++) ---");
var logiResults = new List<Result>();
foreach (var d in all.Where(d => d.VendorID == 0x046D && SupportsLongReport(d, 0x11)))
{
    try { logiResults.AddRange(Hidpp.Probe(d, verbose)); }
    catch (Exception e) { Console.WriteLine($"  {d.ProductID:X4}: erreur {e.GetType().Name}: {e.Message}"); }
}

// ---------- 4. Razer ----------
Console.WriteLine("\n--- Razer ---");
var razerResults = new List<Result>();
foreach (var d in all.Where(d => d.VendorID == 0x1532 && Safe(() => d.GetMaxFeatureReportLength(), 0) >= 90))
{
    try { razerResults.AddRange(Razer.Probe(d, verbose)); }
    catch (Exception e) { Console.WriteLine($"  {d.ProductID:X4}: erreur {e.GetType().Name}: {e.Message}"); }
}

// ---------- 5. Synthèse ----------
Console.WriteLine("\n=== Synthèse ===");
var results = logiResults.Concat(razerResults).ToList();
if (results.Count == 0) Console.WriteLine("Aucune réponse de batterie obtenue.");
foreach (var r in results)
    Console.WriteLine($"{r.Name,-34} {(r.Percent is int p ? p + " %" : "Niveau indisponible"),-20} {r.Charge ?? "",-16} [{r.Source}]");
Console.WriteLine("\n(Comparer ces valeurs avec G HUB / Razer Synapse avant de les considérer valides.)");

static string Describe(HidDevice d)
{
    try
    {
        var desc = d.GetReportDescriptor();
        var tops = desc.DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Distinct().Select(u => $"{u >> 16:X}:{u & 0xFFFF:X}");
        return $"in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feat={d.GetMaxFeatureReportLength()} usages=[{string.Join(" ", tops.Take(4))}]";
    }
    catch { return "(descripteur illisible)"; }
}

static bool SupportsLongReport(HidDevice d, byte id)
{
    try { return d.GetReportDescriptor().OutputReports.Any(r => r.ReportID == id); }
    catch { return false; }
}

static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

record Result(string Name, int? Percent, string? Charge, string Source);

// =====================================================================
// Logitech HID++ 2.0 — requêtes de lecture uniquement
// Référence : spécification des features HID++ 2.0 documentée par la communauté
// (Solaar, libratbag). Pas de documentation officielle publique de Logitech.
// =====================================================================
static class Hidpp
{
    const byte SwId = 0x0A;

    public static IEnumerable<Result> Probe(HidDevice dev, bool verbose)
    {
        if (!dev.TryOpen(out HidStream s)) { Console.WriteLine($"  {dev.ProductID:X4}: ouverture refusée"); yield break; }
        using (s)
        {
            s.ReadTimeout = 600;
            // 0xFF = récepteur/appareil direct ; 1..6 = appareils appairés sur un récepteur
            foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6, 0 })
            {
                var pong = Request(s, idx, 0x00, 1, new byte[] { 0, 0, SwId }, verbose); // IRoot.Ping
                if (pong == null) continue;
                if (pong[2] == 0x8F || pong[2] == 0xFF) { if (verbose) Console.WriteLine($"  idx {idx}: pas HID++ 2.0 / absent"); continue; }

                string name = ReadName(s, idx, verbose) ?? $"Logitech {dev.ProductID:X4} idx{idx}";
                Console.WriteLine($"  {dev.ProductID:X4} idx {idx}: répond (HID++ {pong[4]}.{pong[5]}) -> {name}");

                var res = ReadBattery(s, idx, name, verbose);
                if (res != null) yield return res;
                else Console.WriteLine($"     {name}: aucune feature de batterie lisible");
            }
        }
    }

    static Result? ReadBattery(HidStream s, byte idx, string name, bool v)
    {
        // 0x1004 Unified Battery (GetStatus = fonction 1)
        int f = FeatureIndex(s, idx, 0x1004, v);
        if (f > 0)
        {
            var r = Request(s, idx, (byte)f, 1, Array.Empty<byte>(), v);
            if (r != null && r[2] == f) return new Result(name, r[4], ChargeText(r[6]), "HID++ 0x1004");
        }
        // 0x1000 Battery Level Status (fonction 0)
        f = FeatureIndex(s, idx, 0x1000, v);
        if (f > 0)
        {
            var r = Request(s, idx, (byte)f, 0, Array.Empty<byte>(), v);
            if (r != null && r[2] == f) return new Result(name, r[4], ChargeText(r[6]), "HID++ 0x1000");
        }
        // 0x1001 Battery Voltage : tension seulement -> pas de % (on n'estime pas)
        f = FeatureIndex(s, idx, 0x1001, v);
        if (f > 0)
        {
            var r = Request(s, idx, (byte)f, 0, Array.Empty<byte>(), v);
            if (r != null && r[2] == f)
            {
                int mv = (r[4] << 8) | r[5];
                Console.WriteLine($"     {name}: tension {mv} mV (HID++ 0x1001) — pourcentage non fourni par l'appareil");
                return new Result(name, null, ChargeText(r[6]), "HID++ 0x1001 (mV seul)");
            }
        }
        return null;
    }

    static string ChargeText(byte st) => st switch
    {
        0 => "décharge", 1 => "en charge", 2 => "presque plein", 3 => "plein", 4 => "charge lente",
        5 => "batterie invalide", 6 => "erreur thermique", _ => "autre"
    };

    static int FeatureIndex(HidStream s, byte idx, ushort featureId, bool v)
    {
        var r = Request(s, idx, 0x00, 0, new[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) }, v); // IRoot.GetFeature
        if (r == null || r[2] != 0x00) return -1;
        return r[4]; // 0 = feature absente
    }

    static string? ReadName(HidStream s, byte idx, bool v)
    {
        int f = FeatureIndex(s, idx, 0x0005, v);
        if (f <= 0) return null;
        var c = Request(s, idx, (byte)f, 0, Array.Empty<byte>(), v);
        if (c == null || c[2] != f) return null;
        int len = c[4];
        var sb = new StringBuilder();
        while (sb.Length < len && sb.Length < 64)
        {
            var r = Request(s, idx, (byte)f, 1, new[] { (byte)sb.Length }, v);
            if (r == null || r[2] != f) break;
            int before = sb.Length;
            for (int i = 4; i < r.Length && sb.Length < len; i++) { if (r[i] == 0) break; sb.Append((char)r[i]); }
            if (sb.Length == before) break;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    // Envoie un rapport long HID++ (0x11, 20 octets) et attend la réponse correspondante.
    static byte[]? Request(HidStream s, byte devIdx, byte featIdx, int fn, byte[] parms, bool v)
    {
        var buf = new byte[20];
        buf[0] = 0x11; buf[1] = devIdx; buf[2] = featIdx; buf[3] = (byte)((fn << 4) | SwId);
        Array.Copy(parms, 0, buf, 4, Math.Min(parms.Length, 16));
        try { s.Write(buf, 0, buf.Length); } catch { return null; }

        var end = DateTime.UtcNow.AddMilliseconds(700);
        var rbuf = new byte[Math.Max(s.Device.GetMaxInputReportLength(), 20)];
        while (DateTime.UtcNow < end)
        {
            int n;
            try { n = s.Read(rbuf, 0, rbuf.Length); } catch { return null; }
            if (n < 7) continue;
            if (rbuf[0] != 0x11 && rbuf[0] != 0x10) continue;
            if (rbuf[1] != devIdx) continue;
            var copy = rbuf.AsSpan(0, Math.Min(n, 20)).ToArray();
            // Erreur HID++ 2.0 : featIdx = 0xFF ; HID++ 1.0 : sub-id 0x8F
            if (rbuf[2] == 0xFF || rbuf[2] == 0x8F) { if (v) Console.WriteLine($"     (erreur HID++ idx {devIdx}: {Convert.ToHexString(copy)})"); return copy; }
            if (rbuf[2] == featIdx && (rbuf[3] & 0x0F) == SwId) { if (v) Console.WriteLine($"     <- {Convert.ToHexString(copy)}"); return copy; }
        }
        return null;
    }
}

// =====================================================================
// Razer — protocole feature report 90 octets (requête get_battery uniquement)
// Source : protocole rétro-ingénierié par la communauté (OpenRazer). Non documenté
// par Razer, peut changer selon le modèle et le firmware.
// =====================================================================
static class Razer
{
    public static IEnumerable<Result> Probe(HidDevice dev, bool verbose)
    {
        // Windows interdit l'ouverture en lecture/écriture des collections souris/clavier.
        // On ouvre donc avec un accès nul (requête de périphérique), comme le fait hidapi.
        IntPtr s = Native.CreateFile(dev.DevicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (s == new IntPtr(-1)) { Console.WriteLine($"  {dev.ProductID:X4}: ouverture refusée (erreur {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})"); yield break; }
        string name = dev.GetProductName() ?? $"Razer {dev.ProductID:X4}";
        try
        {
            int len = Math.Max(dev.GetMaxFeatureReportLength(), 91);
            foreach (byte tid in new byte[] { 0x1F, 0xFF, 0x3F, 0x9F })
            {
                var bat = Query(s, len, tid, 0x80, verbose);
                if (bat == null) continue;
                byte status = bat[1];
                if (status != 0x02) { Console.WriteLine($"  {dev.ProductID:X4} tid {tid:X2}: pas de valeur (statut {StatusText(status)})"); continue; }
                int raw = bat[10]; // arguments[1] : 0..255
                var chg = Query(s, len, tid, 0x84, verbose);
                string? charge = chg != null && chg[1] == 0x02 ? (chg[10] == 1 ? "en charge" : "décharge") : null;
                int pct = (int)Math.Round(raw * 100.0 / 255.0);
                Console.WriteLine($"  {dev.ProductID:X4} tid {tid:X2}: brut {raw}/255 -> {pct} %");
                yield return new Result(name, pct, charge, "Razer 0x07/0x80");
                yield break;
            }
            Console.WriteLine($"  {dev.ProductID:X4} {name}: aucune réponse valide sur cette interface");
        }
        finally { Native.CloseHandle(s); }
    }

    static string StatusText(byte s) => s switch { 0x01 => "occupé", 0x03 => "échec", 0x04 => "timeout", 0x05 => "non supporté", _ => $"0x{s:X2}" };

    static byte[]? Query(IntPtr s, int len, byte tid, byte cmdId, bool v)
    {
        var buf = new byte[len];
        buf[0] = 0x00;          // report ID
        buf[2] = tid;           // transaction id
        buf[6] = 2;             // taille des arguments
        buf[7] = 0x07;          // classe de commande : alimentation
        buf[8] = cmdId;         // 0x80 = get battery, 0x84 = get charging status
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= buf[i];
        buf[89] = crc;
        try
        {
            if (!Native.HidD_SetFeature(s, buf, (uint)len)) { if (v) Console.WriteLine($"     (SetFeature échec, erreur {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})"); return null; }
            Thread.Sleep(80);
            var resp = new byte[len];
            resp[0] = 0x00;
            if (!Native.HidD_GetFeature(s, resp, (uint)len)) { if (v) Console.WriteLine($"     (GetFeature échec, erreur {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})"); return null; }
            if (v) Console.WriteLine($"     <- {Convert.ToHexString(resp, 0, 20)}");
            return resp;
        }
        catch (Exception e) { if (v) Console.WriteLine($"     ({e.GetType().Name}: {e.Message})"); return null; }
    }
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(IntPtr h, byte[] buf, uint len);
    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, uint len);
}
