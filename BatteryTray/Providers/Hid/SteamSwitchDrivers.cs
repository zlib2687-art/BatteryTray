// Manettes Valve Steam Controller (VID 28DE) et manettes compatibles Switch Pro (057E:2009), dont la GameSir Tarantula Pro sur son récepteur 2,4 GHz en mode Switch.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/steam_controller.cpp et switch_pro.cpp, porté en C# — voir NOTICE.md.
// Steam Controller : rapports d'entrée de batterie ; disposition de référence : hid-steam de Linux. Récepteur 1142 (2015), câble 1302, Bluetooth 1303, palet 1304 (2026), récepteur interne 1305.
//   Ancien (1142) : [01][00][04][11]… octet 14 = % ; nouveau : [43][état][%] avec état 2 = en charge. Le palet expose quatre emplacements même avec moins de manettes connectées.
// Switch Pro : SDL (SDL_hidapi_switch.c) documente l'octet 2 du rapport d'entrée complet 0x30 : bits 7..5 = niveau de batterie (0..4), bit 4 = en charge. Steam Input ou un autre hôte doit activer les rapports complets : ce pilote ne fait qu'écouter.
namespace BatteryTray.Providers.Hid;

public static class SteamController
{
    public static string? Transport(ushort pid) => pid switch
    {
        0x1142 => "Wireless adapter", 0x1302 => "USB cable", 0x1303 => "Bluetooth", 0x1304 => "Wireless adapter", 0x1305 => "Wireless adapter", _ => null,
    };

    /// <summary>Rapport d'entrée → (pourcentage, en charge) ; null si ce n'est pas un rapport de batterie valide pour ce format.</summary>
    public static (int Percent, bool Charging)? Parse(byte[] r, bool legacy)
    {
        if (r.Length < 15) return null;
        if (legacy) return r[0] != 0x01 || r[1] != 0x00 || r[2] != 0x04 || r[3] != 11 || r[14] > 100 ? null : (r[14], false);
        return r[0] != 0x43 || r[2] > 100 ? null : (r[2], r[1] == 2);
    }

    static (int Percent, bool Charging)? Read(HidDev dev, bool legacy, bool puck)
    {
        bool received = false;
        long deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            // Windows complète les rapports d'entrée numérotés à la taille maximale de la collection.
            var r = dev.Read(puck && !received ? 250 : 100);
            if (r == null) { if (puck && !received) return null; continue; }
            received = true;
            if (Parse(r, legacy) is { } p) return p;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var c in ctx.Colls.ToList())
        {
            if (c.Vid != 0x28DE || c.Page != 0xFF00 || c.InLen < 15) continue;
            if (Transport(c.Pid) is not { } transport) continue;
            bool puck = c.Pid == 0x1304;
            if (puck && c.Usage != 0x01) continue; // on saute la collection des contacts de charge
            using var dev = new HidDev();
            if (!dev.Open(c)) continue;
            bool legacy = c.Pid == 0x1142;
            if (Read(dev, legacy, puck) is not { } p) continue;
            string name = legacy || puck || c.Pid == 0x1305 || c.Product.Length == 0 ? "Steam Controller" : c.Product;
            ctx.Add(new DeviceStatus
            {
                Key = $"steam:{HidUtil.Hex4(c.Pid)}:{PlayStationDriver.PathHash(c.Path)}", Name = name, Kind = DeviceKind.Gamepad, Via = transport, Percent = p.Percent, Charging = p.Charging,
            }, c.Vid, c.Pid);
        }
    }
}

public static class SwitchProDriver
{
    /// <summary>Rapport complet 0x30 : (pourcentage = 25 × niveau, en charge) ; null si trop court, d'un autre type ou niveau &gt; 4.</summary>
    public static (int Percent, bool Charging)? Parse(byte[] r)
    {
        if (r.Length < 13 || r[0] != 0x30) return null;
        int level = (r[2] & 0xE0) >> 5;
        return level > 4 ? null : (25 * level, (r[2] & 0x10) != 0 && level < 4);
    }

    public static void Poll(PollContext ctx)
    {
        const ushort vid = 0x057E, pid = 0x2009;
        foreach (var c in ctx.Find(vid, pid, c => c.Page == 0x01 && (c.Usage == 0x04 || c.Usage == 0x05) && c.InLen >= 13))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            long deadline = Environment.TickCount64 + 350;
            for (long now = Environment.TickCount64; now < deadline; now = Environment.TickCount64)
            {
                var r = d.Read((int)(deadline - now));
                if (r == null) break;
                if (Parse(r) is not { } p) continue;
                // On utilise le chemin HID pour l'identité, comme le pilote PlayStation.
                ctx.Add(new DeviceStatus
                {
                    Key = "switchpro:" + PlayStationDriver.PathHash(c.Path), Name = c.Product.Length > 0 ? c.Product : "Nintendo Switch Pro Controller", Kind = DeviceKind.Gamepad,
                    Via = "Switch mode", Percent = p.Percent, Charging = p.Charging,
                }, vid, pid);
                break;
            }
        }
    }
}
