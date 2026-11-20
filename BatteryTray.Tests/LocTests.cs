using Xunit;

namespace BatteryTray.Tests;

public class LocTests
{
    static IEnumerable<string> Codes => Loc.Languages.Select(l => l.Code);

    [Fact]
    public void Seven_languages_are_available()
    {
        Assert.Equal(new[] { "fr", "ar-TN", "en", "es", "pt", "zh", "ru" }, Codes.ToArray());
    }

    [Fact]
    public void Every_language_has_every_french_key_and_no_empty_text()
    {
        // on lit les clés via Duration/T : une clé manquante retomberait sur le français ou sur la clé elle-même
        string[] keys =
        {
            "app.title", "flyout.empty", "menu.refresh", "menu.settings", "menu.language", "menu.notifyLow", "menu.notifyFull", "menu.exit",
            "state.charging", "state.full", "state.unavailable", "autonomy.calculating", "autonomy.fmt", "autonomy.over",
            "autonomy.atLeast", "autonomy.rated", "unit.d", "unit.h", "unit.min", "time.read",
            "notif.low.title", "notif.low.body", "notif.full.title", "notif.full.body", "device.gamepad",
            "install.title", "install.ask", "install.done", "install.error", "uninstall.ask", "uninstall.data", "uninstall.done",
            "set.title", "set.general", "set.language", "set.poll", "set.pollOpt", "set.startup", "set.notifs", "set.lowThreshold",
            "set.colors", "set.orange", "set.red", "set.devices", "set.col.show", "set.col.device", "set.col.name", "set.col.low",
            "set.col.rated", "set.up", "set.down", "set.hint", "set.ok", "set.cancel",
            "set.size", "set.size.s", "set.size.m", "set.size.l", "set.size.xl", "state.estimated",
            "menu.testNotif", "notif.test.title", "notif.test.body",
            "state.offline", "charge.eta", "part.left", "part.right", "part.other", "chart.none",
        };
        string saved = Loc.Current;
        try
        {
            string french = "";
            foreach (string code in Codes)
            {
                Loc.Set(code);
                foreach (string key in keys)
                {
                    string text = Loc.T(key);
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{code}/{key} vide");
                    Assert.NotEqual(key, text); // la clé n'a pas été trouvée
                }
                if (code == "fr") french = Loc.T("set.hint");
                else Assert.NotEqual(french, Loc.T("set.hint")); // vraiment traduit, pas une copie du français
            }
        }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void Placeholders_are_preserved_in_every_language()
    {
        string saved = Loc.Current;
        try
        {
            foreach (string code in Codes)
            {
                Loc.Set(code);
                Assert.Contains("{0}", Loc.T("autonomy.fmt").Replace("{0}", "{0}"));
                Assert.Contains("{0}", Loc.T("autonomy.atLeast"));
                Assert.Contains("{0}", Loc.T("time.read"));
                Assert.Contains("{0}", Loc.T("notif.low.body"));
                Assert.Contains("{1}", Loc.T("notif.low.body"));
                Assert.Contains("{0}", Loc.T("notif.full.body"));
                Assert.Contains("{1}", Loc.T("notif.full.body"));
                Assert.Contains("{0}", Loc.T("device.gamepad"));
                Assert.Contains("{0}", Loc.T("install.ask"));
                Assert.Contains("{0}", Loc.T("set.pollOpt"));
            }
        }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void Only_tunisian_arabic_is_right_to_left()
    {
        string saved = Loc.Current;
        try
        {
            foreach (string code in Codes)
            {
                Loc.Set(code);
                Assert.Equal(code == "ar-TN", Loc.IsRtl);
            }
        }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void Unknown_language_falls_back_to_french()
    {
        string saved = Loc.Current;
        try
        {
            Loc.Set("xx");
            Assert.Equal("fr", Loc.Current);
        }
        finally { Loc.Set(saved); }
    }

    [Theory]
    [InlineData(0.5, "30 min")]
    [InlineData(1.0, "1 h")]
    [InlineData(5.5167, "5 h 31 min")]
    [InlineData(26.0, "1 j 2 h")]
    [InlineData(48.0, "2 j")]
    public void Duration_is_formatted_to_the_minute(double hours, string expected)
    {
        string saved = Loc.Current;
        try { Loc.Set("fr"); Assert.Equal(expected, Loc.Duration(hours)); }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void Autonomy_text_covers_every_state()
    {
        string saved = Loc.Current;
        try
        {
            Loc.Set("fr");
            var baseReading = new BatteryReading("d", "D", DeviceKind.Mouse, 50, null, null, DateTime.Now);

            Assert.Equal("calcul en cours…", Loc.AutonomyText(baseReading with { AutonomyPending = true }));
            Assert.Equal("reste ≈ 5 h", Loc.AutonomyText(baseReading with { AutonomyHours = 5 }));
            Assert.Equal("reste plus de 10 h", Loc.AutonomyText(baseReading with { AutonomyHours = 10.7, AutonomyLowerBound = true }));
            Assert.Equal("reste plus de 24 h", Loc.AutonomyText(baseReading with { AutonomyHours = 24, AutonomyLowerBound = true }));
            Assert.Equal("reste plus de 24 h", Loc.AutonomyText(baseReading with { AutonomyHours = 70, AutonomyLowerBound = true })); // jamais plus que le plafond
            Assert.Equal("reste ≈ 10 h (annoncée)", Loc.AutonomyText(baseReading with { AutonomyHours = 10, AutonomyFromRating = true }));
            Assert.Equal("reste plus de 4 j", Loc.AutonomyText(baseReading with { AutonomyHours = 200 }));
            Assert.Null(Loc.AutonomyText(baseReading with { Charge = ChargeState.Charging, AutonomyHours = 5 }));
            Assert.Null(Loc.AutonomyText(baseReading with { Percent = null }));
        }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void Lower_bound_is_rounded_down_never_up()
    {
        Assert.Equal(24, Loc.FloorForBound(24.9));
        Assert.True(Loc.FloorForBound(1.99) <= 1.99);
        Assert.True(Loc.FloorForBound(0.1) >= 5.0 / 60); // jamais 0
    }
}
