using System.Xml.Linq;
using Xunit;

namespace BatteryTray.Tests;

public class WatchdogTests : IDisposable
{
    readonly string _oldDir = AppSettings.DataDir;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-watchdog-" + Guid.NewGuid().ToString("N"));

    public WatchdogTests() => AppSettings.DataDir = _dir;

    public void Dispose()
    {
        AppSettings.DataDir = _oldDir;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void IntentionalExitFlagIsWrittenThenCleared()
    {
        Assert.False(File.Exists(Watchdog.FlagPath));
        Watchdog.MarkIntentionalExit();
        Assert.True(File.Exists(Watchdog.FlagPath));
        Watchdog.ClearIntentionalExit();
        Assert.False(File.Exists(Watchdog.FlagPath));
        Watchdog.ClearIntentionalExit(); // sans témoin : aucune erreur
    }

    [Fact]
    public void ScriptChecksFlagAndRunningProcessBeforeRestarting()
    {
        string s = Watchdog.ScriptText();
        int flag = s.IndexOf("arret-voulu.flag", StringComparison.Ordinal);
        int running = s.IndexOf("Win32_Process", StringComparison.Ordinal);
        int start = s.IndexOf("sh.Run", StringComparison.Ordinal);
        Assert.True(flag >= 0 && running > flag && start > running);
        Assert.Contains(", 0, False", s); // fenêtre masquée
    }

    [Fact]
    public void TaskDefinitionIsValidHiddenAndRepeatsEveryTenMinutes()
    {
        var doc = XDocument.Parse(Watchdog.TaskXml(@"C:\x\surveiller.vbs"));
        XNamespace n = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PT10M", doc.Descendants(n + "Interval").Single().Value);
        Assert.Equal("true", doc.Descendants(n + "Hidden").Single().Value);
        Assert.Equal("false", doc.Descendants(n + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Contains("//B", doc.Descendants(n + "Arguments").Single().Value);
    }
}
