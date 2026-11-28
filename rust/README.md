# BatteryTray (version Rust)

Niveau de batterie de périphériques sans fil dans la zone de notification de Windows 11, sans moteur à installer.
Réécriture en Rust de BatteryTray : un exécutable d'environ 1 Mo.

## Appareils pris en charge

**Essayés sur du matériel**, ces trois seulement :

- Razer DeathAdder V4 Pro (et, sans essai, les souris Razer répondant à la même requête de batterie)
- Logitech PRO X 2 LIGHTSPEED (046D:0AF7)
- Logitech G502 LIGHTSPEED (et, sans essai, les appareils HID++ 2.0) : la souris ne donne que sa tension, le pourcentage est
  **estimé** et affiché avec « ~ »

Les protocoles Razer et Logitech ne sont pas officiels : seules des requêtes de lecture sont envoyées.

Sources standard de Windows, toutes marques, **non essayées** (aucun appareil de ce type sur le PC de développement) :
service Batterie Bluetooth LE, niveau de batterie connu de Windows (Bluetooth), périphériques HID qui déclarent leur batterie.
Les manettes sans fil Xbox sur récepteur ne sont pas lues.

Pilotes de marques, portés de l'ancienne version C#, **non essayés** (voir `NOTICE.md`), pour des modèles précis :

- Souris : Glorious, Hitscan, G-Wolves, WLMouse, LAMZU, Pulsar, Kysona, ATK, VXE, Redragon, ASUS ROG, Cooler Master,
  Keychron, MCHOSE, AULA, Rapoo, DAREU, RAWM, AJAZZ, Akko, Arbiter, Areson, CHERRY, Chimera, DARKRIM, BEITONG, Darmoshark,
  Endgame Gear, ENDORFY, EWEADN, Fantech, Finalmouse, Machenike, MSI, Niceboy, Ninjutso, Sprime, NZXT, Press Play, Raigor,
  Scyrox, Sinowealth, VAXEE, Zenith, Zowie et divers modèles génériques
- Casques et micros : SteelSeries, Corsair, HyperX, Razer (BlackShark, Barracuda Pro), ASTRO, Jabra, JBL Quantum, Alienware,
  Audeze, Logitech (G, G435), AKG, Fractal, MAONO, MOONDROP, VGN, Poly / Plantronics, Turtle Beach, Sony INZONE, ATK N9
- Claviers : AULA, MCHOSE, Rongyuan, SONiX, Royal Kludge, NuPhy, Semico, ATK / VGN, VIA / PMO Hub
- Manettes : PlayStation, 8BitDo, GameSir (récepteur et Bluetooth), Flydigi, Steam Controller, Switch Pro
- Bluetooth, appareils appairés seulement : AirPods / Beats, Fast Pair, Galaxy Buds, Nothing / CMF Ear
- Facultatif : Mobile connecté de Microsoft (téléphone), `headsetcontrol.exe` s'il est posé à côté de l'application

Pour les AirPods, Beats et Fast Pair, l'application écoute passivement les annonces Bluetooth ; rien n'est envoyé sur Internet.

## Fonctions

- Une icône par appareil (pictogramme et pourcentage), infobulle
- Souris Logitech et Razer : DPI et fréquence d'interrogation en cours (panneau et infobulle), suivis en continu : relecture
  toutes les 0,4 s environ tant que le clavier ou la souris servent, aucune après 15 s d'inactivité
- Panneau au clic : cartes, autonomie et temps de charge estimés, graphique depuis la dernière charge
- Paramètres (double-clic) : actualisation, démarrage avec Windows, taille, couleurs, seuils, notifications, appareils
- Notifications « batterie faible » et « charge terminée »
- Redémarrage automatique après un plantage (tâche planifiée, sans fenêtre), sauf après « Quitter »
- 20 langues (traductions rédigées par une IA, à faire relire), arabe, persan et hébreu de droite à gauche. Aucun accès réseau, aucune télémétrie, aucun droit administrateur.

Une durée « (annoncée) » vient d'une valeur de départ connue pour le modèle, pas d'une mesure.

## Limites

- Hormis les trois appareils ci-dessus, rien n'est vérifié sur du matériel.
- Un appareil qui ne répond pas à une lecture n'a plus de valeur à jour ; le détail gauche / droite / boîtier des écouteurs
  n'est pas affiché. Pas d'historique sur 30 jours.
- Le suivi du DPI interroge la souris souvent : un effet en jeu n'est pas exclu.
- Mémoire mesurée sur le PC de développement (0.18) : environ 3,4 à 4,5 Mo engagés ; mémoire de travail entre 0,4 et 15 Mo.

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
