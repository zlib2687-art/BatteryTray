using Xunit;

namespace BatteryTray.Tests;

public class LanguagesTests
{
    static readonly string[] Added = { "de", "fa", "he", "ja", "ko", "hu", "pl", "pt-BR", "sv", "vi", "tr", "uk", "zh-TW" };

    static IDictionary<string, string> Table(string code)
    {
        var field = typeof(Loc).GetField("Tables", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        var tables = (System.Collections.IDictionary)field.GetValue(null)!;
        return (IDictionary<string, string>)tables[code]!;
    }

    [Fact]
    public void TwentyLanguagesAreOffered()
    {
        Assert.Equal(20, Loc.Languages.Length);
        Assert.Equal(20, Loc.Languages.Select(l => l.Code).Distinct().Count());
        Assert.Equal(20, Loc.Languages.Select(l => l.Native).Distinct().Count());
        foreach (var code in Added) Assert.Contains(Loc.Languages, l => l.Code == code);
    }

    [Fact]
    public void EveryOfferedLanguageHasACompleteTable()
    {
        var fr = Table("fr");
        foreach (var (code, _) in Loc.Languages)
        {
            var t = Table(code);
            Assert.True(fr.Keys.All(t.ContainsKey), $"{code} : clés manquantes : {string.Join(", ", fr.Keys.Where(k => !t.ContainsKey(k)))}");
            Assert.True(t.Keys.All(fr.ContainsKey), $"{code} : clés en trop : {string.Join(", ", t.Keys.Where(k => !fr.ContainsKey(k)))}");
            Assert.All(t, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{code}/{kv.Key} vide"));
        }
    }

    [Fact]
    public void AddedLanguagesAreReallyTranslatedNotCopiedFromFrench()
    {
        var fr = Table("fr");
        // « OK », « 24 h », « Minimum », « Maximum »… peuvent légitimement rester proches : on tolère une minorité
        foreach (var code in Added)
        {
            var t = Table(code);
            int same = fr.Count(kv => t[kv.Key] == kv.Value);
            Assert.True(same <= 12, $"{code} : {same} textes identiques au français");
        }
    }

    [Fact]
    public void OnlyArabicPersianAndHebrewAreRightToLeft()
    {
        string before = Loc.Current;
        try
        {
            foreach (var (code, _) in Loc.Languages)
            {
                Loc.Set(code);
                Assert.Equal(code is "ar-TN" or "fa" or "he", Loc.IsRtl);
            }
        }
        finally { Loc.Set(before); }
    }

    [Theory]
    [InlineData("zh", "Microsoft YaHei UI")]
    [InlineData("zh-TW", "Microsoft JhengHei UI")]
    [InlineData("ja", "Yu Gothic UI")]
    [InlineData("ko", "Malgun Gothic")]
    [InlineData("fr", "Segoe UI")]
    [InlineData("he", "Segoe UI")]
    public void CjkLanguagesGetADedicatedFont(string code, string font)
    {
        string before = Loc.Current;
        try { Loc.Set(code); Assert.Equal(font, Loc.FontFamily); }
        finally { Loc.Set(before); }
    }

    [Theory]
    [InlineData(0x040C, "fr")]
    [InlineData(0x0409, "en")]
    [InlineData(0x0809, "en")]
    [InlineData(0x0C0A, "es")]
    [InlineData(0x0416, "pt-BR")]
    [InlineData(0x0816, "pt")]
    [InlineData(0x0804, "zh")]
    [InlineData(0x1004, "zh")]
    [InlineData(0x0404, "zh-TW")]
    [InlineData(0x0C04, "zh-TW")]
    [InlineData(0x0407, "de")]
    [InlineData(0x0429, "fa")]
    [InlineData(0x040D, "he")]
    [InlineData(0x0411, "ja")]
    [InlineData(0x0412, "ko")]
    [InlineData(0x040E, "hu")]
    [InlineData(0x0415, "pl")]
    [InlineData(0x041D, "sv")]
    [InlineData(0x042A, "vi")]
    [InlineData(0x041F, "tr")]
    [InlineData(0x0422, "uk")]
    [InlineData(0x0419, "ru")]
    [InlineData(0x0401, "ar-TN")]
    [InlineData(0x0000, "fr")]
    [InlineData(0xFFFF, "fr")]
    public void WindowsLanguageIdsMapToASupportedLanguage(int langId, string expected) => Assert.Equal(expected, Loc.FromLangId(langId));

    [Fact]
    public void EveryDetectedLanguageIsOffered()
    {
        var offered = Loc.Languages.Select(l => l.Code).ToHashSet();
        for (int id = 0; id <= 0xFFFF; id += 1) Assert.Contains(Loc.FromLangId(id), offered);
    }

    [Fact]
    public void FormattedTextWorksInEveryLanguage()
    {
        string before = Loc.Current;
        try
        {
            foreach (var (code, _) in Loc.Languages)
            {
                Loc.Set(code);
                Assert.Contains("Razer", Loc.T("notif.low.body", "Razer", 15));
                Assert.Contains("15", Loc.T("notif.low.body", "Razer", 15));
                Assert.False(string.IsNullOrWhiteSpace(Loc.Duration(5.5)));
                Assert.Contains("5", Loc.Duration(5.5));
            }
        }
        finally { Loc.Set(before); }
    }
}
