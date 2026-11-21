using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class FastPairDriverTests
{
    [Fact]
    public void BatteryFieldIsDecodedForThreeComponents()
    {
        // version 0, champ type 3 (batterie, UI affichée) de longueur 3 : gauche 80 en charge, droite 75, boîtier 0x7F inconnu... ici 60
        var d = new byte[] { 0x00, 0x33, 0x80 | 80, 75, 60 };
        var b = FastPairDriver.Parse(d)!;
        Assert.Equal((80, true, 75, false, 60), (b.Left, b.LeftChg, b.Right, b.RightChg, b.Box));
        var s = FastPairDriver.Summarise(b); Assert.Equal(75, s.Percent); Assert.False(s.Charging); Assert.Equal("L 80% ⚡, R 75%, case 60%", s.Detail);
        Assert.Null(FastPairDriver.Parse(new byte[] { 0x00, 0x14, 0x7F }));                                             // 0x7F = inconnu : rien d'exploitable
        Assert.Null(FastPairDriver.Parse(new byte[] { 0x01, 0x33, 80, 75, 60 }));                                       // version non nulle
        Assert.Null(FastPairDriver.Parse(new byte[] { 0x00, 0x33, 80 }));                                                // longueur annoncée trop grande
        Assert.Null(FastPairDriver.Parse(new byte[] { 0xAA, 0xBB, 0xCC }));                                             // identifiant de modèle découvrable
    }

    [Fact]
    public void ParserSurvivesRandomData()
    {
        var rnd = new Random(221);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 30)]; rnd.NextBytes(r); if (r.Length > 0 && i % 2 == 0) r[0] = 0; var b = FastPairDriver.Parse(r); if (b != null) { var s = FastPairDriver.Summarise(b); Assert.InRange(s.Percent, -1, 100); } }
    }
}

public class AirPodsDriverTests
{
    static byte[] Proximity(ushort model, byte status, byte b6, byte b7)
    {
        var d = new byte[27]; d[0] = 0x07; d[1] = 0x19; d[2] = 0x01; d[3] = (byte)model; d[4] = (byte)(model >> 8); d[5] = status; d[6] = b6; d[7] = b7; return d;
    }

    [Fact]
    public void AirPodsMaxIsASingleBattery()
    {
        var a = AirPodsDriver.Parse(Proximity(0x200A, 0, 0x08, 0x80))!;
        Assert.True(a.Single); Assert.Equal((80, false), (a.Left, a.LeftChg));
        var chg = AirPodsDriver.Parse(Proximity(0x200A, 0, 0x08, 0x90))!; Assert.True(chg.LeftChg);
        Assert.Null(AirPodsDriver.Parse(Proximity(0x200A, 0, 0x0F, 0x80)));                    // inconnu
    }

    [Fact]
    public void DualBudsAreDecodedAndTheLowerDischargingBudIsShown()
    {
        // gauche diffuse (bit 5) : quartet bas = 8 (80 %), haut = 6 (60 %) ; boîtier 9 (90 %) ; droite en charge (bit 1 du quartet haut de l'octet 7)
        var a = AirPodsDriver.Parse(Proximity(0x2014, 0x20, 0x68, 0x29))!;
        Assert.Equal((80, 60, 90, false, true, false), (a.Left, a.Right, a.Box, a.LeftChg, a.RightChg, a.BoxChg));
        var s = AirPodsDriver.Summarise(a); Assert.Equal(80, s.Percent); Assert.False(s.Charging); Assert.Contains("L 80%", s.Detail); Assert.Contains("R 60%⚡", s.Detail);
        Assert.Null(AirPodsDriver.Parse(new byte[] { 7, 0x19, 0, 0x14, 0x20 }));               // trop court
        var pairing = Proximity(0x2014, 0, 0x88, 0); pairing[2] = 0; Assert.Null(AirPodsDriver.Parse(pairing));   // mode appairage
        Assert.Null(AirPodsDriver.Parse(Proximity(0x1234, 0, 0x88, 0)));                        // pas un modèle audio Apple
    }

    [Fact]
    public void BestAdvertPrefersTheModelAndTheStrongestFreshSignal()
    {
        long now = 1_000_000;
        var strong = AirPodsDriver.Parse(Proximity(0x2014, 0x20, 0x88, 0x00))!; strong.Rssi = -50; strong.Tick = now - 1000;
        var weak = AirPodsDriver.Parse(Proximity(0x2014, 0x20, 0x44, 0x00))!; weak.Rssi = -75; weak.Tick = now - 500;
        var other = AirPodsDriver.Parse(Proximity(0x2027, 0x20, 0x22, 0x00))!; other.Rssi = -30; other.Tick = now - 100;
        var adverts = new Dictionary<ulong, AirPodsDriver.Advert> { [1] = strong, [2] = weak, [3] = other };
        Assert.Equal(80, AirPodsDriver.Pick(0x2014, false, adverts, now)!.Left);
        Assert.Null(AirPodsDriver.Pick(0x2014, false, new Dictionary<ulong, AirPodsDriver.Advert> { [2] = weak }, now + 200_000));    // trop ancien
        var distant = AirPodsDriver.Parse(Proximity(0x2014, 0x20, 0x88, 0x00))!; distant.Rssi = -85; distant.Tick = now;
        Assert.Null(AirPodsDriver.Pick(0x2014, false, new Dictionary<ulong, AirPodsDriver.Advert> { [1] = distant }, now));           // trop faible hors connexion
        Assert.NotNull(AirPodsDriver.Pick(0x2014, true, new Dictionary<ulong, AirPodsDriver.Advert> { [1] = distant }, now));
        Assert.True(AirPodsDriver.IsSingleBatteryModel(0x2017)); Assert.False(AirPodsDriver.IsSingleBatteryModel(0x2014));
    }

    [Fact]
    public void ParserSurvivesRandomData()
    {
        var rnd = new Random(222);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 40)]; rnd.NextBytes(r); if (r.Length > 6 && i % 2 == 0) { r[0] = 7; r[1] = 0x19; r[2] = 1; r[4] = 0x20; } var a = AirPodsDriver.Parse(r); if (a != null) { var s = AirPodsDriver.Summarise(a); Assert.InRange(s.Percent, -1, 100); } }
    }
}
