# Notices et attributions

BatteryTray est distribué sous la **licence publique générale GNU version 3** (GPL-3.0) : voir le fichier `LICENSE`.
Les sources des protocoles de la version actuelle sont citées dans [`rust/NOTICE.md`](rust/NOTICE.md).

## Ancienne version C# (historique du dépôt)

Une partie de ses pilotes d'appareils (dossier `BatteryTray/Providers/Hid`) a été **portée en C#** à partir du code du projet
**BatteryHub** (anciennement « Wireless Battery »), par krisgfx9, publié sous GPL-3.0 :
https://github.com/krisgfx9/Wireless-Device-Battery-Monitor

Ce projet cite lui-même ses sources (OpenRazer, HeadsetControl, glorious-indicator, glorious-ctl, les configurateurs web des
fabricants, des captures fournies par des utilisateurs). Pour les pilotes ASUS ROG, Jabra et Cooler Master : peripheral-battery-tray
(MIT, d'après G-Helper), BatteryTool, jabridge, le pilote GNP de HeadsetControl, coolermaster-usb-protocol et MM731-Controller.
Dépendances de cette version : HidSharp 2.1.0 (Apache License 2.0) et l'environnement .NET (MIT).

Toute redistribution d'un code dérivé doit rester sous GPL-3.0 et conserver ces notices.
