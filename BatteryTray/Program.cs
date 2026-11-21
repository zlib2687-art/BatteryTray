using System.Runtime.InteropServices;
using BatteryTray.Providers;
using BatteryTray.Providers.Hid;

namespace BatteryTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppSettings.Current = AppSettings.Load();
        Loc.Init(AppSettings.Current.Language);
        // test visuel d'une langue sans toucher aux réglages : BATTERYTRAY_LANG=ru, ar-TN, zh…
        if (Environment.GetEnvironmentVariable("BATTERYTRAY_LANG") is { Length: > 0 } forcedLanguage) Loc.Set(forcedLanguage);
        TrayManager.OrangeBelow = AppSettings.Current.OrangeBelow;
        TrayManager.RedBelow = AppSettings.Current.RedBelow;

        // Mode aperçu : dessine les icônes dans un PNG puis quitte (aucun appareil lu)
        int i = Array.IndexOf(args, "--preview");
        if (i >= 0 && i + 1 < args.Length)
        {
            using var t = new TrayManager(AppSettings.Current, () => { }, () => { }, _ => { }, () => { }, () => { });
            t.SavePreview(args[i + 1]);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // Une erreur imprévue ne doit ni afficher la boîte d'erreur de .NET ni arrêter l'application : on la note dans le journal.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => TrayApp.LogError("Exception d'interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayApp.LogError("Exception non gérée", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { TrayApp.LogError("Tâche en échec", e.Exception); e.SetObserved(); };

        bool silent = args.Contains("/silent", StringComparer.OrdinalIgnoreCase);

        // « --list [fichier] » (ou l'ancien « --providers fichier ») : lit chaque source une fois et liste les appareils trouvés
        int pv = Array.FindIndex(args, a => a is "--list" or "--providers");
        if (pv >= 0)
        {
            Cli.Output(Cli.BuildList(), pv + 1 < args.Length ? args[pv + 1] : null);
            return 0;
        }

        // « --dump [fichier] » : décrit les périphériques HID connectés (pour signaler un appareil non pris en charge)
        int dp = Array.IndexOf(args, "--dump");
        if (dp >= 0)
        {
            Cli.Output(Cli.BuildDump(), dp + 1 < args.Length ? args[dp + 1] : null);
            return 0;
        }

        // « --demo » : flyout avec des appareils d'exemple, sans matériel
        if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase)) return Cli.RunDemo();

        // « --flyout-preview fichier.png 125 » : dessine le flyout avec des données d'exemple à cette taille (%)
        int fp = Array.IndexOf(args, "--flyout-preview");
        if (fp >= 0 && fp + 2 < args.Length && int.TryParse(args[fp + 2], out int scale))
        {
            AppSettings.Current.FlyoutScale = scale;
            var now = DateTime.Now;
            var sample = DemoData.Readings(now);
            var history = DemoData.History(now); // 80 % -> 72 % en 2 h, pour montrer le mini-graphique
            // « saved » : ce que le flyout montre quand aucun appareil ne répond (valeurs enregistrées, en gris)
            if (args.Contains("saved")) sample = DeviceView.Prepare(Array.Empty<BatteryReading>(), AppSettings.Current, out _);
            using var f = new FlyoutForm { History = _ => history };
            f.SetItems(sample);
            if (args.Contains("open")) f.ExpandForPreview("b");
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
            bmp.Save(args[fp + 1]);
            return 0;
        }

        // « --settings-preview fichier.png [light|dark] [page 0-3] » : dessine l'écran de paramètres dans une image (test visuel)
        int sp = Array.IndexOf(args, "--settings-preview");
        if (sp >= 0 && sp + 1 < args.Length)
        {
            if (args.Contains("light")) SettingsTheme.Forced = true;
            else if (args.Contains("dark")) SettingsTheme.Forced = false;
            int page = args.Skip(sp + 2).Select(a => int.TryParse(a, out int n) ? n : -1).FirstOrDefault(n => n >= 0);
            using var form = new SettingsForm(AppSettings.Current, () => { });
            form.SelectPage(page);
            using var shot = form.RenderPreview();
            shot.Save(args[sp + 1]);
            return 0;
        }

        // « --history-preview fichier.png [light|dark] [0-2] » : dessine la visionneuse d'historique avec 30 jours de données d'exemple
        int hp = Array.IndexOf(args, "--history-preview");
        if (hp >= 0 && hp + 1 < args.Length)
        {
            if (args.Contains("light")) SettingsTheme.Forced = true;
            else if (args.Contains("dark")) SettingsTheme.Forced = false;
            var demo = new BatteryHistory();
            var now = DateTime.Now;
            var rnd = new Random(5);
            for (int k = 30 * 48; k >= 0; k--)
            {
                var t = now.AddMinutes(-30 * k);
                if (rnd.Next(100) < 6) continue; // quelques trous, comme un appareil éteint
                int level = 5 + (k % 240) * 2 / 5; // décharge de 100 % à environ 5 % en 5 jours, puis recharge
                demo.Record(new[] { new BatteryReading("demo", "Logitech PRO X 2", DeviceKind.Headset, Math.Clamp(level, 5, 100), null, null, t) }, t);
            }
            int range = args.Skip(hp + 2).Select(a => int.TryParse(a, out int n) ? n : -1).FirstOrDefault(n => n >= 0);
            using var form = new HistoryForm(demo);
            form.SetRange(range);
            using var shot = form.RenderPreview();
            shot.Save(args[hp + 1]);
            return 0;
        }

        // « BatteryTray.exe --test-notification low|full|test » : demande à l'instance en cours d'envoyer une notification de test
        int tn = Array.IndexOf(args, "--test-notification");
        if (tn >= 0)
        {
            string kind = tn + 1 < args.Length ? args[tn + 1] : "test";
            if (EventWaitHandle.TryOpenExisting(TrayApp.TestNotificationEvent(kind), out var target)) { target.Set(); return 0; }
            return 1; // application non lancée, ou type inconnu
        }

        // « BatteryTray.exe --settings » : ouvre l'écran de paramètres. Si l'application tourne déjà, on le lui demande
        // (elle garde ses réglages en mémoire : une seconde fenêtre séparée aurait ses modifications écrasées par la première).
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(TrayApp.OpenSettingsEvent, out var running)) { running.Set(); return 0; }
            Application.Run(new SettingsForm(AppSettings.Current, () => { }));
            return 0;
        }

        // Installation / désinstallation (même fichier : voir Installer)
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)) return Installer.Uninstall(silent);
        if (Installer.IsSetupFile()) return Installer.Install(silent);

        // Une seule instance à la fois
        using var mutex = new Mutex(true, @"Local\BatteryTray.SingleInstance", out bool created);
        if (!created) return 0;
        // la clé « Démarrer avec Windows » manque alors que le réglage est actif : on la recrée (installée seulement si c'est bien l'exemplaire installé)
        try { if (AppSettings.Current.StartWithWindows && !Installer.IsStartupEnabled() && Installer.RunsFromInstallDir()) Installer.SetStartup(true); } catch { }
        Watchdog.ClearIntentionalExit(); // on tourne : un futur arrêt sans « Quitter » sera donc considéré comme inattendu

        Application.Run(new TrayApp());
        return 0;
    }
}

sealed class TrayApp : ApplicationContext
{
    /// <summary>Évènement nommé : « BatteryTray.exe --settings » le déclenche pour ouvrir la fenêtre de l'instance en cours.</summary>
    public const string OpenSettingsEvent = @"Local\BatteryTray.OpenSettings";
    public static string TestNotificationEvent(string kind) => $@"Local\BatteryTray.TestNotification.{kind}";

    /// <summary>
    /// Envoie une notification de test : « low » (batterie faible), « full » (charge terminée) ou « test » (message générique).
    /// Utilise les vrais textes de l'application, dans la langue choisie, avec un appareil réel.
    /// </summary>
    void SendTestNotification(string kind) => _ui.BeginInvoke(() =>
    {
        var sample = _latest.FirstOrDefault(r => !r.Offline) ?? _latest.FirstOrDefault();
        string name = sample?.Name ?? "BatteryTray";
        bool shown = kind switch
        {
            "low" => _tray.NotifyAny(Loc.T("notif.low.title"), Loc.T("notif.low.body", name, 15), warning: true),
            "full" => _tray.NotifyAny(Loc.T("notif.full.title"), Loc.T("notif.full.body", name, 100), warning: false),
            _ => _tray.NotifyAny(Loc.T("notif.test.title"), Loc.T("notif.test.body"), warning: false),
        };
        Log($"Notification de test « {kind} » : {(shown ? "envoyée à Windows" : "aucune icône visible")}");
    });

    readonly AppSettings _settings = AppSettings.Current;
    readonly TrayManager _tray;
    readonly Notifier _notifier;
    readonly System.Windows.Forms.Timer _timer = new();

    // Génériques (toutes marques) d'abord, puis modules propriétaires optionnels
    readonly LogitechHidppProvider _logitech = new();
    readonly IBatteryProvider[] _providers;

    // Une souris sans fil dort entre deux mouvements et ne répond alors à aucune requête : tant qu'un appareil
    // Logitech est en veille, on le réessaie souvent (lecture légère, Logitech seulement) pour le saisir à son réveil.
    readonly System.Windows.Forms.Timer _sleepTimer = new() { Interval = 20 * 1000 };
    readonly Dictionary<IBatteryProvider, List<BatteryReading>> _byProvider = new();

    // Contrôle invisible créé sur le thread d'interface : les icônes de notification DOIVENT être
    // créées et modifiées sur ce thread (sinon Windows les retire quand l'autre thread se termine).
    readonly Control _ui = new();
    readonly AutonomyEstimator _estimator = new(Path.Combine(AppSettings.DataDir, "historique.json"));
    readonly AutonomyRatchet _ratchet = new(Path.Combine(AppSettings.DataDir, "autonomie-affichee.json"));
    FlyoutForm? _flyout;
    SettingsForm? _settingsForm;
    HistoryForm? _historyForm;
    readonly BatteryHistory _history = new(Path.Combine(AppSettings.DataDir, "battery_history.json"));
    IReadOnlyList<BatteryReading> _latest = Array.Empty<BatteryReading>();
    readonly RefreshGate _gate = new(TimeSpan.FromMinutes(2));

    // Détection immédiate : un périphérique branché/débranché ou une sortie de veille relance une lecture
    readonly DeviceWatcher _watcher = new();
    readonly System.Windows.Forms.Timer _watchTimer = new();
    DateTime _lastRefreshStart = DateTime.MinValue;

    public TrayApp()
    {
        _ = _ui.Handle; // force la création du handle sur le thread d'interface
        // Ordre = priorité en cas de doublon : modules propres à une marque d'abord (les plus précis), puis les sources
        // génériques toutes marques, le balayage général des périphériques Windows en dernier.
        _providers = new IBatteryProvider[]
        {
            new ProX2Provider(), _logitech, new RazerProvider(), new HidDriversProvider(),
            new BluetoothProvider(), new WindowsBatteryProvider(), new GamepadProvider(), new PnpBatteryProvider(),
        };
        _tray = new TrayManager(_settings, () => Refresh(), ExitThread, SetLanguage, () => _settings.Save(), OpenSettings,
            () => SendTestNotification("test"));
        _tray.Reserve(_settings.Known.Keys.OrderBy(k => k, StringComparer.Ordinal)); // ordre de création stable = épinglage stable
        _notifier = new Notifier(_settings, _tray);
        _tray.IconClicked += ToggleFlyout;
        _tray.IconDoubleClicked += () => { _flyout?.Dismiss(); OpenSettings(); }; // double-clic sur une icône : paramètres
        _tray.HistoryRequested += OpenHistory;
        _timer.Interval = Math.Max(1, _settings.PollMinutes) * 60 * 1000;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _sleepTimer.Tick += (_, _) =>
        {
            if (_latest.Any(r => r.Offline && r.Id.StartsWith("logi-", StringComparison.Ordinal)))
                Refresh(new IBatteryProvider[] { _logitech });
        };
        _sleepTimer.Start();

        _watcher.Changed += () =>
        {
            _watchTimer.Stop();                                   // les évènements arrivent par rafales : on regroupe
            _watchTimer.Interval = RefreshPolicy.DelayMs(DateTime.Now, _lastRefreshStart);
            _watchTimer.Start();
        };
        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); Refresh(); };

        // ouverture des paramètres demandée par une autre instance (« --settings »)
        var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEvent);
        new Thread(() => { while (openSettings.WaitOne()) OpenSettings(); }) { IsBackground = true, Name = "OpenSettings" }.Start();

        // notifications de test demandées par une autre instance (« --test-notification low|full|test »)
        string[] kinds = { "low", "full", "test" };
        var testEvents = kinds.Select(k => new EventWaitHandle(false, EventResetMode.AutoReset, TestNotificationEvent(k))).ToArray();
        new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(testEvents);
                SendTestNotification(kinds[i]);
            }
        }) { IsBackground = true, Name = "TestNotification" }.Start();

        Refresh();
    }

    void SetLanguage(string code)
    {
        _settings.Language = code;
        _settings.Save();
        // reconstruit le menu après la fin du clic en cours (on ne modifie pas un menu pendant son propre clic)
        _ui.BeginInvoke(ApplyLanguageAndColors);
    }

    /// <summary>Applique langue, couleurs et intervalle (après un changement de langue ou de paramètres).</summary>
    void ApplyLanguageAndColors()
    {
        Loc.Set(string.IsNullOrEmpty(_settings.Language) ? Loc.DetectSystem() : _settings.Language);
        TrayManager.OrangeBelow = _settings.OrangeBelow;
        TrayManager.RedBelow = _settings.RedBelow;
        _timer.Interval = Math.Max(1, _settings.PollMinutes) * 60 * 1000;
        _tray.BuildMenu();
        _tray.Update(_latest);
        _flyout?.RefreshLayout(); // taille choisie dans les paramètres
    }

    void OpenHistory()
    {
        _ui.BeginInvoke(() =>
        {
            if (_historyForm is { IsDisposed: false }) { _historyForm.Activate(); return; }
            _historyForm = new HistoryForm(_history);
            _historyForm.FormClosed += (_, _) => { _historyForm.Dispose(); _historyForm = null; Trim(); };
            _historyForm.Show();
            _historyForm.Activate();
        });
    }

    void OpenSettings()
    {
        _ui.BeginInvoke(() =>
        {
            if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
            _settingsForm = new SettingsForm(_settings, () =>
            {
                ApplyLanguageAndColors();
                Refresh(); // nouveaux noms / appareils masqués / ordre pris en compte tout de suite
            });
            _settingsForm.FormClosed += (_, _) => { _settingsForm.Dispose(); _settingsForm = null; Trim(); };
            _settingsForm.Show();
            _settingsForm.Activate();
        });
    }

    void ToggleFlyout()
    {
        if (_flyout == null)
        {
            _flyout = new FlyoutForm { History = id => _estimator.SamplesFor(id) };
            _flyout.SettingsRequested += OpenSettings;
        }
        if (_flyout.Visible) { _flyout.Toggle(_latest); Trim(); return; }
        _flyout.Toggle(_latest);
        Refresh(); // données fraîches pendant que la fenêtre est ouverte
    }

    /// <param name="only">Sources à relire (les autres gardent leur dernière lecture) ; toutes par défaut.</param>
    void Refresh(IBatteryProvider[]? only = null)
    {
        long ticket = _gate.TryEnter(DateTime.Now); // une lecture à la fois ; une lecture bloquée plus de 2 min est abandonnée
        if (ticket == 0) return;
        _lastRefreshStart = DateTime.Now;
        Task.Run(() =>
        {
            List<BatteryReading> withAutonomy;
            try
            {
                foreach (var p in only ?? _providers)
                {
                    try { _byProvider[p] = p.ReadAll().ToList(); }
                    catch (Exception e) { Log($"{p.Name}: {e.GetType().Name}: {e.Message}"); _byProvider[p] = new List<BatteryReading>(); }
                }
                // une même source ne compte qu'une fois : le même appareil vu par deux sources est dédoublonné
                var all = ReadingMerge.Deduplicate(_providers.Where(_byProvider.ContainsKey).SelectMany(p => _byProvider[p]));
                withAutonomy = _estimator.Apply(all); // sur tous les appareils, même masqués
            }
            catch (Exception e)
            {
                // sans ce filet, une exception ici laissait la lecture « en cours » pour toujours : plus aucune mise à jour
                Log($"Lecture: {e.GetType().Name}: {e.Message}");
                _gate.Exit(ticket);
                return;
            }

            try
            {
                _ui.BeginInvoke(() =>
                {
                    try
                    {
                        // masquage, noms personnalisés, ordre : voir DeviceView
                        var view = _ratchet.Apply(DeviceView.Prepare(withAutonomy, _settings, out bool changed)); // la durée ne remonte jamais en décharge
                        if (changed) _settings.Save();
                        _latest = view;
                        _tray.Update(view);
                        _history.Record(view, DateTime.Now); // historique sur 30 jours (une mesure toutes les 30 min au plus)
                        _notifier.Evaluate(view);
                        if (_flyout is { Visible: true }) _flyout.SetItems(view);
                        foreach (var r in view)
                            Log($"{r.Name}: {(r.Percent?.ToString() ?? "indisponible")} {r.Charge} {r.Detail} autonomie={Describe(r)}");
                    }
                    catch (Exception e) { Log($"Mise à jour des icônes: {e.GetType().Name}: {e.Message}"); }
                    finally { _gate.Exit(ticket); }
                    Trim();
                });
            }
            catch (Exception e)
            {
                // fenêtre déjà détruite (fermeture en cours) : on libère la place sans rien faire de plus
                Log($"Affichage: {e.GetType().Name}: {e.Message}");
                _gate.Exit(ticket);
            }
        });
    }

    static string Describe(BatteryReading r) =>
        (r.Offline ? "hors ligne " : "") +
        (r.ChargeEtaHours is double eta ? $"plein dans {eta:F1} h " : "") +
        (r.AutonomyHours is double h
            ? $"{(r.AutonomyLowerBound ? ">" : "≈")}{h:F1} h{(r.AutonomyFromRating ? " (annoncée)" : "")}"
            : r.AutonomyPending ? "en cours" : "-");

    /// <summary>Rend à Windows la mémoire inutilisée entre deux lectures (l'application dort 99 % du temps).</summary>
    static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    protected override void ExitThreadCore()
    {
        Watchdog.MarkIntentionalExit(); // « Quitter » ou fin de session : la tâche de surveillance ne doit pas relancer
        _settings.Save(); // enregistre les dernières valeurs connues (affichage hors ligne)
        _ratchet.Flush();
        _history.Flush();
        _historyForm?.Dispose();
        _estimator.Flush(); // et l'historique d'autonomie (écrit au plus toutes les 5 min le reste du temps)
        _sleepTimer.Dispose();
        _watchTimer.Dispose();
        _watcher.Dispose();
        _timer.Dispose();
        _settingsForm?.Dispose();
        _flyout?.Dispose();
        _tray.Dispose();
        base.ExitThreadCore();
    }

    public static void LogError(string what, Exception? e) =>
        Log($"{what}: {e?.GetType().Name}: {e?.Message}");

    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string file = Path.Combine(AppSettings.DataDir, "journal.txt");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file); // journal local, borné
            // une ligne = une entrée : un nom d'appareil contenant un saut de ligne ne peut pas fabriquer de fausses lignes
            string clean = line.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            if (clean.Length > 400) clean = clean[..400] + "…";
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {clean}{Environment.NewLine}");
        }
        catch { /* le journal est facultatif */ }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
