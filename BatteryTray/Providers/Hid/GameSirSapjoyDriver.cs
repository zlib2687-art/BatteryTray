// GameSir Tarantula Pro / NovaPro (Super Nova, T4NPro) : batterie par la trame « infos appareil » SAPJoy (page vendeur 0xFFA0).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/gamesir_sapjoy.cpp et gamesir_sapjoy_protocol.*, porté en C# — voir NOTICE.md.
// Ces manettes apparaissent comme une manette Xbox filaire pour XInput : le niveau se lit sur leur collection vendeur (page 0xFFA0, rapport de sortie 0x07 / 32 octets,
// rapport d'entrée 0x06 / 64 octets, pas de canal de fonctionnalité) avec la requête de lecture GetDeviceInfo qu'envoie le client GameSir :
//   hôte → manette : [07][long][cmd][sous-cmd][charge…] complétée à 32 octets ; manette → hôte : [06][long][cmd][sous-cmd][charge…] ; long compte lui-même, sans l'identifiant.
//   GetDeviceInfo : cmd 0x0A, sous-cmd 0x01 ; réponse [4] id, [5..6] micrologiciel, [7..12] MAC, [13] batterie (0..100 %, FF = en charge, > 100 ramené à 100), [14] type.
// Trames relevées sur le client GameSir (NovaPro) et son décodeur main.js ; confirmé sur matériel (0x60 = 96 %, comme l'interface de GameSir). Rien n'écrit de profil ni d'éclairage.
// 3537:103E est partagé par le Tarantula Pro et le NovaPro sur leurs récepteurs : le nom vient de la chaîne produit. 3537:100E = NovaPro sur câble.
// Nova / Nova 2 / Nova 2 Lite (1098, 100F, 1040) et Cyclone 2 (100B) sont traités par GameSirDriver.
namespace BatteryTray.Providers.Hid;

public static class GameSirSapjoyDriver
{
    const ushort Vid = 0x3537;

    public sealed record Reading(byte[] Address, int Percent, bool Charging, byte DeviceId, byte DeviceType);

    public static byte[] BuildDeviceInfoRequest()
    {
        var r = new byte[32];
        r[0] = 0x07; r[1] = 0x03; r[2] = 0x0A; r[3] = 0x01;
        return r;
    }

    /// <summary>Décode un rapport d'entrée ; null pour tout ce qui n'est pas une réponse GetDeviceInfo (le contrôleur émet d'autres trames sur la même collection).</summary>
    public static Reading? DecodeDeviceInfo(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x06 || r[2] != 0x0A || r[3] != 0x01) return null;
        if (r[1] < 14 || r[1] + 1 > r.Length) return null;
        var addr = r[7..13];
        byte b = r[13];
        if (b == 0xFF) return new Reading(addr, -1, true, r[4], r[14]); // en charge : aucun pourcentage, l'appelant garde la dernière valeur
        return new Reading(addr, Math.Min((int)b, 100), false, r[4], r[14]);
    }

    /// <summary>Clé d'identité : l'adresse si elle est concrète (ni 00… ni FF…), sinon le PID — jamais le seul identifiant de modèle partagé.</summary>
    public static string Identity(Reading r, ushort pid)
    {
        if (r.Address.All(x => x == 0) || r.Address.All(x => x == 0xFF)) return "gamesir:" + HidUtil.Hex4(pid);
        return "gamesir:sapjoy:" + string.Concat(r.Address.Select(x => x.ToString("X2")));
    }

    public static void Poll(PollContext ctx)
    {
        int first = ctx.Out.Count;
        foreach (ushort pid in new ushort[] { 0x100E, 0x103E, 0x1050 })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            // 0xFFA0 est la page que la bibliothèque du fabricant vérifie : les pages clavier / manette expirent ou refusent l'écriture.
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFFA0 && c.OutLen == 32 && c.InLen == 64 && c.FeatLen == 0))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                if (!d.Write(BuildDeviceInfoRequest())) continue;
                Reading? reading = null;
                long end = Environment.TickCount64 + 900;
                for (; ; )
                {
                    int left = (int)(end - Environment.TickCount64);
                    if (left <= 0) break;
                    var r = d.Read(Math.Min(left, 200));
                    if (r == null) break;
                    if (r.Length < 15) continue; // les trames d'état sont plus courtes ou ont un autre en-tête
                    reading = DecodeDeviceInfo(r);
                    if (reading != null) break;
                }
                if (reading == null) continue;
                var s = new DeviceStatus
                {
                    Key = Identity(reading, pid), Name = c.Product.Length > 0 ? c.Product : "GameSir controller", Kind = DeviceKind.Gamepad,
                    Via = pid == 0x100E ? "USB cable" : "2.4 GHz receiver", Charging = reading.Charging,
                };
                if (!reading.Charging && reading.Percent >= 0) s.Percent = reading.Percent;
                if (!ctx.Out.Skip(first).Any(o => o.Key == s.Key)) ctx.Add(s, Vid, pid); else ctx.Claim(Vid, pid);
                break;
            }
        }
    }
}
