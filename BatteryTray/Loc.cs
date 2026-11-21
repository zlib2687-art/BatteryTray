using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>
/// Textes de l'interface en 7 langues : français, arabe tunisien (de droite à gauche), anglais,
/// espagnol, portugais (Portugal), chinois simplifié et russe. Changement de langue immédiat.
/// Une clé absente dans une langue retombe sur le français.
/// </summary>
public static partial class Loc
{
    public static readonly (string Code, string Native)[] Languages =
    {
        ("fr", "Français"),
        ("ar-TN", "العربية التونسية"),
        ("en", "English"),
        ("es", "Español"),
        ("pt", "Português"),
        ("zh", "中文（简体）"),
        ("ru", "Русский"),
        // langues ajoutées (traductions dans Loc.More.cs), celles de BatteryHub en plus
        ("de", "Deutsch"),
        ("fa", "فارسی"),
        ("he", "עברית"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("hu", "Magyar"),
        ("pl", "Polski"),
        ("pt-BR", "Português (Brasil)"),
        ("sv", "Svenska"),
        ("vi", "Tiếng Việt"),
        ("tr", "Türkçe"),
        ("uk", "Українська"),
        ("zh-TW", "繁體中文"),
    };

    public static string Current { get; private set; } = "fr";
    /// <summary>Langues qui s'écrivent de droite à gauche : arabe, persan, hébreu.</summary>
    public static bool IsRtl => Current is "ar-TN" or "fa" or "he";

    /// <summary>Police adaptée à la langue : les écritures chinoise, japonaise et coréenne ont besoin d'une police dédiée.</summary>
    public static string FontFamily => Current switch
    {
        "zh" => "Microsoft YaHei UI",
        "zh-TW" => "Microsoft JhengHei UI",
        "ja" => "Yu Gothic UI",
        "ko" => "Malgun Gothic",
        _ => "Segoe UI",
    };

    public static void Init(string? saved) => Set(string.IsNullOrEmpty(saved) ? DetectSystem() : saved);

    public static void Set(string code)
    {
        Current = Tables.ContainsKey(code) ? code : "fr";
    }

    /// <summary>Langue de l'interface Windows, si elle fait partie des langues prises en charge (sinon le français).</summary>
    public static string DetectSystem() => FromLangId(GetUserDefaultUILanguage());

    /// <summary>Langue prise en charge pour un identifiant de langue Windows (LANGID) : logique pure, testée.</summary>
    public static string FromLangId(int langId)
    {
        int primary = langId & 0x3FF, sub = (langId >> 10) & 0x3F;
        return primary switch
        {
            0x0C => "fr", 0x09 => "en", 0x0A => "es", 0x19 => "ru", 0x01 => "ar-TN", 0x07 => "de", 0x29 => "fa", 0x0D => "he",
            0x11 => "ja", 0x12 => "ko", 0x0E => "hu", 0x15 => "pl", 0x1D => "sv", 0x2A => "vi", 0x1F => "tr", 0x22 => "uk",
            0x16 => sub == 1 ? "pt-BR" : "pt",            // 0x0416 = Brésil, 0x0816 = Portugal
            0x04 => sub is 2 or 4 ? "zh" : "zh-TW", // 0x0804 Chine, 0x1004 Singapour = simplifié ; 0x0404 Taïwan, 0x0C04 Hong Kong, 0x1404 Macao = traditionnel
            _ => "fr",
        };
    }
    public static string T(string key)
    {
        if (Tables[Current].TryGetValue(key, out var s)) return s;
        return Tables["fr"].TryGetValue(key, out var f) ? f : key;
    }

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    public static string ChargeText(string? code) => code switch
    {
        ChargeState.Charging => T("state.charging"),
        ChargeState.Full => T("state.full"),
        _ => "",
    };

    /// <summary>« plein dans ≈ 40 min » pour un appareil en charge dont la vitesse de charge est connue, sinon null.</summary>
    public static string? ChargeEtaText(BatteryReading r)
    {
        if (!r.IsCharging || r.Percent is null or >= 100 || r.ChargeEtaHours is not double h) return null;
        return T("charge.eta", Duration(h));
    }

    /// <summary>« gauche 80 % · droite 75 % » pour un appareil à plusieurs batteries, sinon null.</summary>
    public static string? PartsText(BatteryReading r)
    {
        if (r.Parts is not { Count: > 1 } parts) return null;
        return string.Join(" · ", parts.Select(p => $"{PartName(p.Label)} {p.Percent} %"));
    }

    static string PartName(string label) => label switch
    {
        BatteryParts.Left => T("part.left"),
        BatteryParts.Right => T("part.right"),
        _ => T("part.other"),
    };

    /// <summary>Au-delà de cette durée, l'autonomie s'affiche « plus de 30 j ».</summary>
    public const double MaxShownHours = 24 * 30;

    /// <summary>Texte d'autonomie à afficher, ou null (en charge / pas de pourcentage).</summary>
    public static string? AutonomyText(BatteryReading r)
    {
        if (r.Percent == null || r.Charge is ChargeState.Charging or ChargeState.Full) return null;
        if (r.AutonomyPending) return T("autonomy.calculating");
        if (r.AutonomyHours is not double h) return null;

        string text;
        if (r.AutonomyLowerBound)
        {
            // plafonné à 24 h : « plus de 24 h » ne bouge plus au fil des lectures
            string bound = h >= AutonomyEstimator.MaxLowerBoundHours ? $"{AutonomyEstimator.MaxLowerBoundHours:0} {T("unit.h")}" : Duration(FloorForBound(h));
            text = T("autonomy.atLeast", bound);
        }
        // Souris à très longue autonomie (DeathAdder V4 Pro : ~150 h) : on montre l'estimation réelle, pas un plafond de 4 j ;
        // « plus de 30 j » seulement au-delà de 720 h.
        else if (h > MaxShownHours) text = T("autonomy.atLeast", Duration(MaxShownHours));
        else text = T("autonomy.fmt", Duration(h));
        return r.AutonomyFromRating ? text + " " + T("autonomy.rated") : text;
    }

    /// <summary>Arrondit une borne basse vers le bas (heures entières, ou pas de 5 min sous 2 h) : on ne promet jamais plus.</summary>
    public static double FloorForBound(double hours) =>
        !double.IsFinite(hours) ? 5.0 / 60
        : hours >= 2 ? Math.Floor(hours) : Math.Max(5.0 / 60, Math.Floor(hours * 12) / 12);

    /// <summary>Durée à la minute près : « 5 h 31 min », « 2 j 5 h », « 40 min ».</summary>
    public static string Duration(double hours)
    {
        // valeur invalide (NaN, infinie, négative, démesurée) : jamais de texte absurde ni de dépassement d'entier
        hours = double.IsNaN(hours) ? 0 : Math.Clamp(hours, 0, 24 * 999);
        int total = Math.Max(1, (int)Math.Round(hours * 60));
        int d = total / 1440, h = total % 1440 / 60, m = total % 60;
        var parts = new List<string>();
        if (d > 0)
        {
            parts.Add($"{d} {T("unit.d")}");
            if (h > 0) parts.Add($"{h} {T("unit.h")}");
        }
        else
        {
            if (h > 0) parts.Add($"{h} {T("unit.h")}");
            if (m > 0 || h == 0) parts.Add($"{m} {T("unit.min")}");
        }
        return string.Join(" ", parts);
    }

    [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

    // ------------------------------------------------------------------------------------------
    // Traductions rédigées pour ce projet. Relecture par un locuteur natif recommandée,
    // surtout pour l'arabe tunisien (dialecte) et le portugais (version du Portugal).
    // ------------------------------------------------------------------------------------------
    static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["fr"] = new()
        {
            ["app.title"] = "Périphériques",
            ["flyout.empty"] = "Aucun appareil avec niveau de batterie détecté.",
            ["menu.refresh"] = "Actualiser maintenant",
            ["menu.language"] = "Langue",
            ["menu.notifyLow"] = "Notification de batterie faible",
            ["menu.notifyFull"] = "Notification de charge terminée",
            ["menu.exit"] = "Quitter",
            ["state.charging"] = "en charge",
            ["state.full"] = "chargée",
            ["state.unavailable"] = "Niveau indisponible",
            ["autonomy.calculating"] = "calcul en cours…",
            ["autonomy.fmt"] = "reste ≈ {0}",
            ["autonomy.over"] = "reste plus de 4 j",
            ["unit.d"] = "j", ["unit.h"] = "h", ["unit.min"] = "min",
            ["time.read"] = "lu à {0}",
            ["notif.low.title"] = "Batterie faible",
            ["notif.low.body"] = "{0} : {1} % restant.",
            ["notif.full.title"] = "Charge terminée",
            ["notif.full.body"] = "{0} est chargé à {1} %.",
            ["device.gamepad"] = "Manette {0}",
            ["install.title"] = "Installation de BatteryTray",
            ["install.ask"] = "Installer BatteryTray pour votre compte Windows ?\n\nLe programme sera copié dans « {0} » et se lancera au démarrage de Windows.",
            ["install.done"] = "BatteryTray est installé et lancé.\nCherchez ses icônes à côté de l'horloge (menu ^).",
            ["install.error"] = "L'installation a échoué : {0}",
            ["uninstall.ask"] = "Désinstaller BatteryTray ?",
            ["uninstall.data"] = "Supprimer aussi les réglages et l'historique (%AppData%\\BatteryTray) ?",
            ["uninstall.done"] = "BatteryTray est désinstallé.",
        },
        ["en"] = new()
        {
            ["app.title"] = "Devices",
            ["flyout.empty"] = "No device reporting a battery level was found.",
            ["menu.refresh"] = "Refresh now",
            ["menu.language"] = "Language",
            ["menu.notifyLow"] = "Low battery notification",
            ["menu.notifyFull"] = "Charging complete notification",
            ["menu.exit"] = "Exit",
            ["state.charging"] = "charging",
            ["state.full"] = "charged",
            ["state.unavailable"] = "Level unavailable",
            ["autonomy.calculating"] = "calculating…",
            ["autonomy.fmt"] = "≈ {0} left",
            ["autonomy.over"] = "≈ more than 4 d left",
            ["unit.d"] = "d", ["unit.h"] = "h", ["unit.min"] = "min",
            ["time.read"] = "read at {0}",
            ["notif.low.title"] = "Low battery",
            ["notif.low.body"] = "{0}: {1}% left.",
            ["notif.full.title"] = "Charging complete",
            ["notif.full.body"] = "{0} is charged to {1}%.",
            ["device.gamepad"] = "Controller {0}",
            ["install.title"] = "BatteryTray setup",
            ["install.ask"] = "Install BatteryTray for your Windows account?\n\nThe program will be copied to \"{0}\" and start with Windows.",
            ["install.done"] = "BatteryTray is installed and running.\nLook for its icons next to the clock (^ menu).",
            ["install.error"] = "Installation failed: {0}",
            ["uninstall.ask"] = "Uninstall BatteryTray?",
            ["uninstall.data"] = "Also delete settings and history (%AppData%\\BatteryTray)?",
            ["uninstall.done"] = "BatteryTray has been uninstalled.",
        },
        ["es"] = new()
        {
            ["app.title"] = "Dispositivos",
            ["flyout.empty"] = "No se encontró ningún dispositivo con nivel de batería.",
            ["menu.refresh"] = "Actualizar ahora",
            ["menu.language"] = "Idioma",
            ["menu.notifyLow"] = "Notificación de batería baja",
            ["menu.notifyFull"] = "Notificación de carga completa",
            ["menu.exit"] = "Salir",
            ["state.charging"] = "cargando",
            ["state.full"] = "cargada",
            ["state.unavailable"] = "Nivel no disponible",
            ["autonomy.calculating"] = "calculando…",
            ["autonomy.fmt"] = "quedan ≈ {0}",
            ["autonomy.over"] = "quedan más de 4 d",
            ["unit.d"] = "d", ["unit.h"] = "h", ["unit.min"] = "min",
            ["time.read"] = "leído a las {0}",
            ["notif.low.title"] = "Batería baja",
            ["notif.low.body"] = "{0}: queda {1} %.",
            ["notif.full.title"] = "Carga completa",
            ["notif.full.body"] = "{0} está cargado al {1} %.",
            ["device.gamepad"] = "Mando {0}",
            ["install.title"] = "Instalación de BatteryTray",
            ["install.ask"] = "¿Instalar BatteryTray para su cuenta de Windows?\n\nEl programa se copiará en «{0}» y se iniciará con Windows.",
            ["install.done"] = "BatteryTray está instalado y en ejecución.\nBusque sus iconos junto al reloj (menú ^).",
            ["install.error"] = "Error de instalación: {0}",
            ["uninstall.ask"] = "¿Desinstalar BatteryTray?",
            ["uninstall.data"] = "¿Eliminar también los ajustes y el historial (%AppData%\\BatteryTray)?",
            ["uninstall.done"] = "BatteryTray se ha desinstalado.",
        },
        ["pt"] = new()
        {
            ["app.title"] = "Dispositivos",
            ["flyout.empty"] = "Nenhum dispositivo com nível de bateria foi encontrado.",
            ["menu.refresh"] = "Atualizar agora",
            ["menu.language"] = "Idioma",
            ["menu.notifyLow"] = "Notificação de bateria fraca",
            ["menu.notifyFull"] = "Notificação de carga concluída",
            ["menu.exit"] = "Sair",
            ["state.charging"] = "a carregar",
            ["state.full"] = "carregada",
            ["state.unavailable"] = "Nível indisponível",
            ["autonomy.calculating"] = "a calcular…",
            ["autonomy.fmt"] = "restam ≈ {0}",
            ["autonomy.over"] = "restam mais de 4 d",
            ["unit.d"] = "d", ["unit.h"] = "h", ["unit.min"] = "min",
            ["time.read"] = "lido às {0}",
            ["notif.low.title"] = "Bateria fraca",
            ["notif.low.body"] = "{0}: restam {1} %.",
            ["notif.full.title"] = "Carga concluída",
            ["notif.full.body"] = "{0} está carregado a {1} %.",
            ["device.gamepad"] = "Comando {0}",
            ["install.title"] = "Instalação do BatteryTray",
            ["install.ask"] = "Instalar o BatteryTray para a sua conta do Windows?\n\nO programa será copiado para «{0}» e iniciará com o Windows.",
            ["install.done"] = "O BatteryTray está instalado e em execução.\nProcure os ícones junto ao relógio (menu ^).",
            ["install.error"] = "Falha na instalação: {0}",
            ["uninstall.ask"] = "Desinstalar o BatteryTray?",
            ["uninstall.data"] = "Eliminar também as definições e o histórico (%AppData%\\BatteryTray)?",
            ["uninstall.done"] = "O BatteryTray foi desinstalado.",
        },
        ["zh"] = new()
        {
            ["app.title"] = "设备",
            ["flyout.empty"] = "未找到报告电量的设备。",
            ["menu.refresh"] = "立即刷新",
            ["menu.language"] = "语言",
            ["menu.notifyLow"] = "低电量通知",
            ["menu.notifyFull"] = "充电完成通知",
            ["menu.exit"] = "退出",
            ["state.charging"] = "充电中",
            ["state.full"] = "已充满",
            ["state.unavailable"] = "电量不可用",
            ["autonomy.calculating"] = "计算中…",
            ["autonomy.fmt"] = "约剩 {0}",
            ["autonomy.over"] = "约剩 4 天以上",
            ["unit.d"] = "天", ["unit.h"] = "小时", ["unit.min"] = "分",
            ["time.read"] = "读取于 {0}",
            ["notif.low.title"] = "电量低",
            ["notif.low.body"] = "{0}：剩余 {1}%。",
            ["notif.full.title"] = "充电完成",
            ["notif.full.body"] = "{0} 已充至 {1}%。",
            ["device.gamepad"] = "手柄 {0}",
            ["install.title"] = "BatteryTray 安装",
            ["install.ask"] = "为你的 Windows 帐户安装 BatteryTray？\n\n程序将复制到“{0}”，并随 Windows 启动。",
            ["install.done"] = "BatteryTray 已安装并运行。\n请在时钟旁（^ 菜单）查找其图标。",
            ["install.error"] = "安装失败：{0}",
            ["uninstall.ask"] = "卸载 BatteryTray？",
            ["uninstall.data"] = "同时删除设置和历史记录（%AppData%\\BatteryTray）？",
            ["uninstall.done"] = "BatteryTray 已卸载。",
        },
        ["ru"] = new()
        {
            ["app.title"] = "Устройства",
            ["flyout.empty"] = "Не найдено устройств с данными об уровне заряда.",
            ["menu.refresh"] = "Обновить сейчас",
            ["menu.language"] = "Язык",
            ["menu.notifyLow"] = "Уведомление о низком заряде",
            ["menu.notifyFull"] = "Уведомление о завершении зарядки",
            ["menu.exit"] = "Выход",
            ["state.charging"] = "заряжается",
            ["state.full"] = "заряжено",
            ["state.unavailable"] = "Уровень недоступен",
            ["autonomy.calculating"] = "расчёт…",
            ["autonomy.fmt"] = "осталось ≈ {0}",
            ["autonomy.over"] = "осталось более 4 дн.",
            ["unit.d"] = "дн.", ["unit.h"] = "ч", ["unit.min"] = "мин",
            ["time.read"] = "считано в {0}",
            ["notif.low.title"] = "Низкий заряд",
            ["notif.low.body"] = "{0}: осталось {1} %.",
            ["notif.full.title"] = "Зарядка завершена",
            ["notif.full.body"] = "{0} заряжено до {1} %.",
            ["device.gamepad"] = "Геймпад {0}",
            ["install.title"] = "Установка BatteryTray",
            ["install.ask"] = "Установить BatteryTray для вашей учётной записи Windows?\n\nПрограмма будет скопирована в «{0}» и будет запускаться вместе с Windows.",
            ["install.done"] = "BatteryTray установлен и запущен.\nИщите значки рядом с часами (меню ^).",
            ["install.error"] = "Сбой установки: {0}",
            ["uninstall.ask"] = "Удалить BatteryTray?",
            ["uninstall.data"] = "Удалить также настройки и историю (%AppData%\\BatteryTray)?",
            ["uninstall.done"] = "BatteryTray удалён.",
        },
        ["ar-TN"] = new()
        {
            ["app.title"] = "الأجهزة",
            ["flyout.empty"] = "ما لقينا حتى جهاز يبيّن مستوى البطارية.",
            ["menu.refresh"] = "حدّث توّا",
            ["menu.language"] = "اللغة",
            ["menu.notifyLow"] = "تنبيه البطارية الضعيفة",
            ["menu.notifyFull"] = "تنبيه كمال الشحن",
            ["menu.exit"] = "اخرج",
            ["state.charging"] = "قاعد يشحن",
            ["state.full"] = "مشحون",
            ["state.unavailable"] = "المستوى موش متوفّر",
            ["autonomy.calculating"] = "الحساب جاري…",
            ["autonomy.fmt"] = "باقي تقريبًا {0}",
            ["autonomy.over"] = "باقي أكثر من 4 أيام",
            ["unit.d"] = "ي", ["unit.h"] = "س", ["unit.min"] = "د",
            ["time.read"] = "آخر قراية {0}",
            ["notif.low.title"] = "البطارية ضعيفة",
            ["notif.low.body"] = "{0} : باقي {1} %.",
            ["notif.full.title"] = "الشحن كمّل",
            ["notif.full.body"] = "{0} شحن حتى {1} %.",
            ["device.gamepad"] = "يد التحكم {0}",
            ["install.title"] = "تثبيت BatteryTray",
            ["install.ask"] = "نثبّتو BatteryTray على حسابك في Windows ؟\n\nالبرنامج باش يتنسخ في «{0}» وباش يخدم مع تشغيل Windows.",
            ["install.done"] = "BatteryTray تثبّت وقاعد يخدم.\nالقى الأيقونات حذا الساعة (قائمة ^).",
            ["install.error"] = "التثبيت فشل : {0}",
            ["uninstall.ask"] = "نمسحو BatteryTray ؟",
            ["uninstall.data"] = "نمسحو زادة الإعدادات والتاريخ (%AppData%\\BatteryTray) ؟",
            ["uninstall.done"] = "BatteryTray تمسح.",
        },
    };
}
