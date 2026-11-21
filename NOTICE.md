# Notices et attributions

BatteryTray est distribué sous la **licence publique générale GNU version 3** (GPL-3.0) : voir le fichier `LICENSE`.

## Pourquoi la GPL-3.0

Une partie des pilotes d'appareils de BatteryTray (dossier `BatteryTray/Providers/Hid`) est **portée en C#** à partir du code du projet
**BatteryHub** (anciennement « Wireless Battery »), par krisgfx9, publié sous GPL-3.0 :
https://github.com/krisgfx9/Wireless-Device-Battery-Monitor

Le code dérivé d'une œuvre GPL-3.0 doit rester sous GPL-3.0 : c'est pourquoi tout le projet l'est. Chaque fichier concerné rappelle,
en en-tête, de quel fichier de BatteryHub il est dérivé. Les identifiants matériels (VID:PID) et les formats de trames viennent de
ce projet, qui cite lui-même ses sources (OpenRazer, HeadsetControl, glorious-indicator, glorious-ctl, les configurateurs web des
fabricants, et des captures fournies par des utilisateurs).

## Ce que cela implique

- Vous pouvez utiliser, étudier, modifier et redistribuer BatteryTray, y compris dans un cadre commercial, **à condition** de
  fournir le code source de votre version sous la même licence (GPL-3.0) et de conserver ces notices.
- Aucune garantie : voir les articles 15 et 16 de la licence.

## Dépendances

- HidSharp 2.1.0 (Apache License 2.0) : accès aux périphériques HID.
- Environnement .NET (MIT).
