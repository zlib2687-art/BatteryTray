# BatteryTray (version Rust)

Niveau de batterie de périphériques sans fil dans la zone de notification de Windows 11, sans moteur à installer.
Réécriture en Rust de BatteryTray : environ 500 Ko, quelques Mo de mémoire engagée.

## Appareils pris en charge

Uniquement ceux-ci, les seuls essayés :

- Razer DeathAdder V4 Pro (et, sans essai, les souris Razer répondant à la même requête de batterie)
- Logitech PRO X 2 LIGHTSPEED (046D:0AF7)
- Logitech G502 LIGHTSPEED (et, sans essai, les appareils HID++ 2.0) : la souris ne donne que sa tension, le pourcentage est
  **estimé** et affiché avec « ~ »

Les protocoles Razer et Logitech ne sont pas officiels : seules des requêtes de lecture sont envoyées.

## Fonctions

- Une icône par appareil (pictogramme et pourcentage), infobulle
- Panneau au clic : cartes, autonomie et temps de charge estimés, graphique depuis la dernière charge
- Paramètres (double-clic) : actualisation, démarrage avec Windows, taille, couleurs, seuils, notifications, appareils
- Notifications « batterie faible » et « charge terminée »
- Redémarrage automatique après un plantage (tâche planifiée, sans fenêtre), sauf après « Quitter »
- En français seulement. Aucun accès réseau, aucune télémétrie, aucun droit administrateur.

Une durée « (annoncée) » vient d'une valeur de départ connue pour le modèle, pas d'une mesure.

## Compiler

```powershell
cargo +stable-x86_64-pc-windows-gnu test --release
cargo +stable-x86_64-pc-windows-gnu build --release
```

Copiez `target\release\battery-tray.exe` sous un nom finissant par `Setup.exe` pour en faire l'installateur.
Il s'installe dans `%LocalAppData%\Programs\BatteryTrayRust` ; données dans `%AppData%\BatteryTrayRust`.
Désinstallation : `BatteryTray.exe --uninstall`, quitter, puis supprimer le dossier. Le programme n'est pas signé.

## Licence

GPL-3.0 (fichier `LICENSE`). Voir `NOTICE.md`.
