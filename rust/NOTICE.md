# Notices

Cette version est un portage en Rust de BatteryTray (C#, retiré du dépôt mais consultable dans son historique), distribuée sous la même licence GPL-3.0.

- Protocole de batterie Razer : rétro-ingénierie de la communauté (projet OpenRazer).
- Protocole du casque Logitech PRO X 2 : décrit par la communauté (patch du noyau Linux citant HeadsetControl).
- HID++ (souris Logitech) : spécification issue de la communauté (Solaar, libratbag).
- DPI et fréquence d'interrogation : commandes Razer d'après OpenRazer, fonctions HID++ d'après Solaar.

## Pilotes de marques (`src/brands`)

Portés en Rust des pilotes C# de BatteryTray, eux-mêmes **dérivés de BatteryHub** (« Wireless Battery »), par krisgfx9, publié
sous GPL-3.0 : https://github.com/krisgfx9/Wireless-Device-Battery-Monitor

Chaque fichier indique en tête le fichier d'origine et les sources des protocoles (HeadsetControl, rivalcfg, steel-mouse,
OpenRGB, OpenRazer, HaloBattery de HeyOkay sous licence MIT, pilotes Linux, entre autres). Aucun n'a été essayé sur du matériel
avec cette version ; seul le démarrage de l'écoute des annonces Bluetooth a été essayé sur une vraie radio.

Dépendance : `windows-sys` (MIT ou Apache-2.0).

Aucun lien avec Razer ou Logitech ; les noms de produits appartiennent à leurs propriétaires.
