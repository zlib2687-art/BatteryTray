# BatteryTray

Affiche le niveau de batterie de vos périphériques sans fil dans la zone de notification de Windows 11, à côté de l'indicateur
de langue : un pictogramme avec le pourcentage, un panneau détaillé au clic, des notifications et une autonomie estimée.

100 % hors ligne, aucune télémétrie, aucun droit administrateur.

## Installation

Téléchargez [BatteryTray-Rust-Setup.exe](https://github.com/zlib2687-art/BatteryTray/releases/download/v0.3.0-rust/BatteryTray-Rust-Setup.exe)
(environ 500 Ko) et lancez-le. Ni Rust ni .NET à installer. Le fichier n'est pas signé : Windows peut afficher un avertissement
SmartScreen. La somme de contrôle est sur la page de la
[Release](https://github.com/zlib2687-art/BatteryTray/releases/tag/v0.3.0-rust).

## Appareils et fonctions

La version publiée (0.3) prend en charge le Razer DeathAdder V4 Pro, le Logitech PRO X 2 et le G502 LIGHTSPEED, en français.
Détails, limites et compilation : [`rust/`](rust/README.md).

## Ancienne version C#

La version C# (.NET) a été retirée de ce dépôt. Son code reste consultable dans
[l'historique](https://github.com/zlib2687-art/BatteryTray/tree/a34f646), sans suivi ni correctifs.
Si elle est installée sur votre PC, désinstallez-la avant d'installer la version Rust : les deux ne peuvent pas tourner en même temps.

## Publier sans information personnelle

`powershell -ExecutionPolicy Bypass -File verifier-avant-publication.ps1` cherche, dans les fichiers qui seraient publiés, les
informations propres à votre PC et à votre identité Git.

## Licence

GPL-3.0 (fichier `LICENSE`). Voir `NOTICE.md`. Aucune garantie.
