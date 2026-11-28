# BatteryTray

Affiche le niveau de batterie de vos périphériques sans fil dans la zone de notification de Windows 11, à côté de l'indicateur
de langue : un pictogramme avec le pourcentage, un panneau détaillé au clic, des notifications et une autonomie estimée.

Aucun accès à Internet, aucune télémétrie, aucun droit administrateur.

## Installation

Téléchargez [BatteryTray-Rust-Setup.exe](https://github.com/zlib2687-art/BatteryTray/releases/download/v0.18.0-rust/BatteryTray-Rust-Setup.exe)
(environ 1 Mo) et lancez-le. Ni Rust ni .NET à installer. Le fichier n'est pas signé : Windows peut afficher un avertissement
SmartScreen. La somme de contrôle est sur la page de la
[Release](https://github.com/zlib2687-art/BatteryTray/releases/tag/v0.18.0-rust).

## Ce que fait la version 0.18

- **Appareils** : de nombreuses marques à récepteur propriétaire (souris, casques, claviers, manettes, micros), les écouteurs
  Bluetooth appairés et les sources de batterie standard de Windows. **Seuls le Razer DeathAdder V4 Pro, le Logitech PRO X 2 et
  le G502 LIGHTSPEED ont été essayés sur du matériel** ; [liste complète](rust/README.md#appareils-pris-en-charge).
- **20 langues** (traductions rédigées par une IA, à faire relire), dont l'arabe, le persan et l'hébreu de droite à gauche.
- **Souris Logitech et Razer** : DPI et fréquence d'interrogation, suivis en continu.
- Panneau, paramètres, autonomie estimée, notifications, redémarrage automatique.

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
