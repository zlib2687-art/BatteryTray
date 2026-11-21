// Écouteurs Samsung Galaxy Buds et Nothing / CMF Ear, interrogés par leur service RFCOMM (liaison Bluetooth classique).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/galaxy_buds.cpp, galaxy_buds_protocol.h, nothing_ear.cpp et nothing_ear_protocol.h, porté en C# (WinRT à la place des sockets Winsock) — voir NOTICE.md.
// Windows n'affiche pas de niveau de batterie pour eux. Seuls les appareils appairés que Windows déclare connectés et dont le nom correspond sont essayés (jamais un appareil non connecté : la requête expirerait). Si l'application du
// fabricant tient le canal RFCOMM, les écouteurs sont ignorés.
//  * Galaxy Buds (Buds, Buds+, Live, Pro, Buds2, Buds2 Pro, FE, Core, Buds3 / 3 Pro / 3 FE…) : acceptent une connexion RFCOMM sur le service SPP de Samsung et poussent une trame d'état (gauche, droite, boîtier) dès l'ouverture ;
//    rien ne leur est envoyé. Buds2 et suivants : {2e73a4ad-332d-41fc-90e2-16bef06523f2} ; Buds+, Live, Pro : SPP standard {00001101-0000-1000-8000-00805f9b34fb} ; Buds d'origine : {00001102-0000-1000-8000-00805f9b34fd}, trame héritée.
//    UUID de service de GalaxyBudsClient (Model/Constants.cs et fichiers DeviceSpec par modèle).
//    Trame (Buds+ et suivants) : FD [taille lo][taille hi | drapeaux] [id] [charge…] [crc lo][crc hi] DD ; (Buds d'origine) : FE [type] [taille] [id] [charge…] [crc lo][crc hi] EE ; taille = id + charge + crc (10 bits bas de l'en-tête de 16 bits ;
//    bit 0x2000 = fragment) ; crc = CRC-16/XMODEM (poly 0x1021, init 0) sur id + charge, petit-boutiste. Messages de batterie (poussés) : 0x60 STATUS_UPDATED : charge[1] = % gauche, [2] = % droit, [6] = % boîtier (pas sur les Buds d'origine) ;
//    0x61 EXTENDED_STATUS_UPDATED : charge[2] = gauche, [3] = droit, [7] = boîtier. Un niveau 0 = partie non connectée ; les Buds+ annoncent 101 pour un boîtier inconnu. Source : GalaxyBudsClient (timschneeb), SppMessage.cs, Crc16.cs, décodeurs d'état.
//  * Nothing / CMF (Ear (3a), Ear (a), Ear (2), Ear, CMF Buds…) : service NT Link {aeac4a03-dff5-498f-843a-34487cf133eb}, commande de batterie 0xC007 en lecture seule.
//    Trame : 55 60 01 [cmd lo][cmd hi] [long. charge] 00 [seq] [charge…] [crc lo][crc hi] ; crc = CRC-16/MODBUS (init 0xFFFF, poly réfléchi 0xA001) sur les octets [0 .. 8+long−1], LE ; requête = cmd 0xC007, charge vide ; réponse = cmd 0xC007
//    (ou poussée 0xE001) : charge[0] = nombre N d'appareils, suivi de N paires (id, état) : id 0x02 gauche, 0x03 droite, 0x04 boîtier ; état : bit 7 en charge, bits 0..6 pourcentage. Source : protocole Nothing / CMF déduit par la communauté
//    (radiance-project/ear-web, Vadim-Karpenko/ear-tray, pedrordgs/nothing-ear).
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace BatteryTray.Providers.Hid;

public static class GalaxyBudsProtocol
{
    public sealed class Status { public int Left = -1, Right = -1, CaseLevel = -1; }

    public static ushort Crc16(byte[] data, int offset, int size)
    {
        ushort crc = 0;
        for (int i = 0; i < size; i++)
        {
            crc ^= (ushort)(data[offset + i] << 8);
            for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    static int Level(byte v) => v is >= 1 and <= 100 ? v : -1;

    /// <summary>Cherche dans le flot reçu les trames d'état et garde les niveaux de la dernière valide. <paramref name="legacy"/> : cadrage des Galaxy Buds d'origine.</summary>
    public static Status? FindStatus(byte[] data, bool legacy)
    {
        byte som = legacy ? (byte)0xFE : (byte)0xFD, eom = legacy ? (byte)0xEE : (byte)0xDD;
        Status? result = null;
        for (int i = 0; i + 6 <= data.Length; i++)
        {
            if (data[i] != som) continue;
            int len;
            if (legacy) len = data[i + 2];
            else
            {
                int header = data[i + 1] | (data[i + 2] << 8);
                if ((header & 0x2000) != 0) continue; // les fragments ne portent que des données de micrologiciel
                len = header & 0x3FF;
            }
            if (len < 3 || i + 3 + len >= data.Length || data[i + 3 + len] != eom) continue;
            int body = i + 3; // id, charge, crc
            ushort crc = (ushort)(data[body + len - 2] | (data[body + len - 1] << 8));
            if (Crc16(data, body, len - 2) != crc) continue;
            byte id = data[body]; int p = body + 1, plen = len - 3;
            var s = new Status();
            if (id == 0x61 && plen >= 4) { s.Left = Level(data[p + 2]); s.Right = Level(data[p + 3]); if (!legacy && plen >= 8) s.CaseLevel = Level(data[p + 7]); }
            else if (id == 0x60 && plen >= 3) { s.Left = Level(data[p + 1]); s.Right = Level(data[p + 2]); if (!legacy && plen >= 7) s.CaseLevel = Level(data[p + 6]); }
            else continue;
            result = s;
            i += 3 + len; // on saute la fin de cette trame
        }
        return result;
    }
}

public static class NothingEarProtocol
{
    public sealed class Status { public int Left = -1, Right = -1, CaseLevel = -1; public bool LeftCharging, RightCharging, CaseCharging; }

    public static ushort Crc16Modbus(byte[] data, int offset, int size)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < size; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte[] BuildBatteryQuery(byte seq = 1)
    {
        var f = new byte[] { 0x55, 0x60, 0x01, 0x07, 0xC0, 0x00, 0x00, seq, 0x00, 0x00 };
        ushort crc = Crc16Modbus(f, 0, 8);
        f[8] = (byte)(crc & 0xFF); f[9] = (byte)(crc >> 8);
        return f;
    }

    public static Status? FindStatus(byte[] data)
    {
        Status? result = null;
        for (int i = 0; i + 10 <= data.Length; i++)
        {
            if (data[i] != 0x55 || data[i + 1] != 0x60 || data[i + 2] != 0x01) continue;
            int plen = data[i + 5], total = 8 + plen + 2;
            if (i + total > data.Length) continue;
            ushort crc = (ushort)(data[i + 8 + plen] | (data[i + 9 + plen] << 8));
            if (Crc16Modbus(data, i, 8 + plen) != crc) continue;
            int cmd = data[i + 3] | (data[i + 4] << 8);
            if (cmd != 0xC007 && cmd != 0xE001) { i += total - 1; continue; }
            int p = i + 8;
            if (plen < 1) continue;
            int count = data[p];
            if (1 + count * 2 > plen) continue;
            var s = new Status(); bool any = false;
            for (int k = 0; k < count; k++)
            {
                byte id = data[p + 1 + k * 2], st = data[p + 2 + k * 2];
                int pct = st & 0x7F; bool chg = (st & 0x80) != 0;
                if (pct > 100) continue;
                if (id == 0x02) { s.Left = pct; s.LeftCharging = chg; any = true; }
                else if (id == 0x03) { s.Right = pct; s.RightCharging = chg; any = true; }
                else if (id == 0x04) { s.CaseLevel = pct; s.CaseCharging = chg; any = true; }
            }
            if (any) result = s;
            i += total - 1;
        }
        return result;
    }
}

/// <summary>Client RFCOMM minimal sur WinRT : ouvre un service par adresse et UUID, envoie éventuellement une requête, lit jusqu'à ce que <c>done</c> reconnaisse le flot.</summary>
public static class RfcommClient
{
    public static byte[]? Exchange(ulong address, Guid service, byte[]? request, Func<byte[], bool> done, int connectMs, int readMs)
    {
        try { return Task.Run(() => ExchangeAsync(address, service, request, done, connectMs, readMs)).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    static async Task<byte[]?> ExchangeAsync(ulong address, Guid service, byte[]? request, Func<byte[], bool> done, int connectMs, int readMs)
    {
        using var dev = await BluetoothDevice.FromBluetoothAddressAsync(address);
        if (dev == null) return null;
        var found = await dev.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(service), BluetoothCacheMode.Uncached);
        if (found.Error != BluetoothError.Success || found.Services.Count == 0) return null;
        using var svc = found.Services[0];
        using var socket = new StreamSocket();
        using (var cts = new CancellationTokenSource(connectMs))
            await socket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName).AsTask(cts.Token);
        if (request != null)
        {
            using var writer = new DataWriter(socket.OutputStream);
            writer.WriteBytes(request);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        using var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        var buf = new List<byte>();
        long end = Environment.TickCount64 + readMs;
        while (Environment.TickCount64 < end && buf.Count < 8192)
        {
            using var cts = new CancellationTokenSource((int)Math.Max(1, end - Environment.TickCount64));
            uint n;
            try { n = await reader.LoadAsync(512).AsTask(cts.Token); } catch (OperationCanceledException) { break; }
            if (n == 0) break;
            var chunk = new byte[n]; reader.ReadBytes(chunk); buf.AddRange(chunk);
            if (done(buf.ToArray())) break;
        }
        return buf.ToArray();
    }
}

public static class GalaxyBudsDriver
{
    static readonly Guid SppNew = new("2e73a4ad-332d-41fc-90e2-16bef06523f2"), SppStandard = new("00001101-0000-1000-8000-00805f9b34fb"), SppLegacy = new("00001102-0000-1000-8000-00805f9b34fd");

    public static bool LooksLikeBuds(string name) => name.Contains("buds", StringComparison.OrdinalIgnoreCase);

    static string Pct(int v) => v >= 0 ? v + "%" : "-";

    public static (int Percent, string Detail) Summarise(int left, int right, int caseLevel)
    {
        int pct = left >= 0 && right >= 0 ? Math.Min(left, right) : left >= 0 ? left : right; // la zone de notification montre le plus bas des deux écouteurs ; l'infobulle liste les trois parties
        string detail = $"L {Pct(left)}, R {Pct(right)}" + (caseLevel >= 0 ? $", case {Pct(caseLevel)}" : "");
        return (pct, detail);
    }

    static GalaxyBudsProtocol.Status? ReadStatus(ulong addr, Guid service, bool legacy)
    {
        var data = RfcommClient.Exchange(addr, service, null, d => GalaxyBudsProtocol.FindStatus(d, legacy) != null, 4000, 3000);
        return data == null ? null : GalaxyBudsProtocol.FindStatus(data, legacy);
    }

    public static void Poll(PollContext ctx)
    {
        List<(ulong Addr, string Name, bool Connected)> paired;
        try { paired = FastPairDriver.PairedDevices(); } catch { return; }
        foreach (var (addr, name, connected) in paired)
        {
            if (!connected || !LooksLikeBuds(name)) continue; // jamais un appareil non connecté
            var st = ReadStatus(addr, SppNew, false) ?? ReadStatus(addr, SppStandard, false) ?? ReadStatus(addr, SppLegacy, true);
            if (st == null) continue;
            var (pct, detail) = Summarise(st.Left, st.Right, st.CaseLevel);
            ctx.Out.Add(new DeviceStatus { Key = "galaxybuds:" + addr.ToString("X12"), Name = name, Kind = DeviceKind.Headset, Via = "Bluetooth", Percent = pct, Detail = detail });
        }
    }
}

public static class NothingEarDriver
{
    static readonly Guid NtLink = new("aeac4a03-dff5-498f-843a-34487cf133eb");

    public static bool LooksLikeNothingEar(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("nothing") && (n.Contains("ear") || n.Contains("buds"))) return true;
        return n.Contains("ear (") || n.Contains("ear 3a") || n.Contains("cmf buds");
    }

    public static void Poll(PollContext ctx)
    {
        List<(ulong Addr, string Name, bool Connected)> paired;
        try { paired = FastPairDriver.PairedDevices(); } catch { return; }
        foreach (var (addr, name, connected) in paired)
        {
            if (!connected || !LooksLikeNothingEar(name)) continue;
            var data = RfcommClient.Exchange(addr, NtLink, NothingEarProtocol.BuildBatteryQuery(1), d => NothingEarProtocol.FindStatus(d) != null, 3000, 2000);
            if (data == null || NothingEarProtocol.FindStatus(data) is not { } st) continue;
            var (pct, detail) = GalaxyBudsDriver.Summarise(st.Left, st.Right, st.CaseLevel);
            ctx.Out.Add(new DeviceStatus { Key = "nothingear:" + addr.ToString("X12"), Name = name, Kind = DeviceKind.Headset, Via = "Bluetooth", Percent = pct, Charging = st.LeftCharging || st.RightCharging, Detail = detail });
        }
    }
}
