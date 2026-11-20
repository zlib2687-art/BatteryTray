using System.Text.RegularExpressions;

namespace BatteryTray;

/// <summary>Devine le type d'un appareil d'après son nom (toutes marques, plusieurs langues). Logique pure, testée.</summary>
public static class DeviceNames
{
    // « micro » comme mot entier : « Microsoft Mouse » ne doit pas devenir un microphone
    static readonly Regex MicName = new(
        @"\b(mic|micro|microphone|lavalier|lav|rode|hollyland)\b|microphone|dji mic|wireless go",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DeviceKind Guess(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("airpods")
            || n.Contains("casque") || n.Contains("écouteur") || n.Contains("earbud") || n.Contains("earphone")
            || n.Contains("auricular") || n.Contains("fone") || n.Contains("наушник") || n.Contains("耳机")) return DeviceKind.Headset;
        if (MicName.IsMatch(n)) return DeviceKind.Microphone;
        if (n.Contains("mouse") || n.Contains("souris") || n.Contains("ratón") || n.Contains("rato") || n.Contains("мышь")
            || n.Contains("鼠标") || n.Contains("mx master") || n.Contains("trackball") || n.Contains("trackpad")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("clavier") || n.Contains("teclado") || n.Contains("клавиатур")
            || n.Contains("键盘") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("manette") || n.Contains("mando") || n.Contains("xbox")
            || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("joy-con")
            || n.Contains("геймпад") || n.Contains("手柄")) return DeviceKind.Gamepad;
        if (n.Contains("webcam") || n.Contains("camera") || n.Contains("caméra") || n.Contains("streamcam")) return DeviceKind.Webcam;
        return DeviceKind.Other;
    }
}
