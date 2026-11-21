# BatteryTray

Affiche le niveau de batterie de vos périphériques sans fil dans la zone de notification de Windows 11,
à côté de l'indicateur de langue : un pictogramme (casque, souris, clavier, manette, webcam) avec le
pourcentage, un flyout détaillé au clic, des notifications et une autonomie estimée.

100 % hors ligne, aucune télémétrie, aucun droit administrateur, aucune modification du système.

## Installation

1. Lancez `dist\BatteryTray-Setup.exe` (double-clic).
2. Le programme est copié dans `%LocalAppData%\Programs\BatteryTray`, se lance avec Windows, crée un
   raccourci dans le menu Démarrer et une tâche planifiée de **redémarrage automatique** (voir plus bas).
   Désinstallation : **Paramètres → Applications** (elle retire aussi la tâche).
3. **Épinglez les icônes** : Windows 11 range les nouvelles icônes dans le menu **^**. Ouvrez
   *Paramètres → Personnalisation → Barre des tâches → Autres icônes de barre d'état système* et activez
   BatteryTray (ou glissez les icônes du menu ^ vers la barre). Une application ne peut pas le faire à votre place.

Le fichier fait environ **69 Mo** (il embarque .NET, compressé : rien d'autre à installer). Il n'est pas signé : Windows peut
afficher un avertissement SmartScreen (« Informations complémentaires » → « Exécuter quand même »).

Installation silencieuse : `BatteryTray-Setup.exe /silent`.

**Version portable** : `dist\BatteryTray-Portable.zip` contient `BatteryTray.exe`, à lancer directement, sans installation
ni droit administrateur (pas de démarrage avec Windows ni de redémarrage automatique : ils viennent de l'installation).
Les deux fichiers sont produits par `build-installer.ps1`.

## Utilisation

| Action | Effet |
|---|---|
| Survoler une icône | Infobulle : nom, pourcentage, « en charge » ou autonomie, heure de lecture |
| Clic gauche sur une icône | Flyout « Périphériques » (une carte par appareil) |
| **Double-clic gauche** sur une icône | Ferme le flyout et ouvre les **paramètres** |
| Clic sur une carte du flyout | Déplie un **mini-graphique** du niveau depuis la dernière charge (échelle adaptée aux valeurs mesurées) |
| Engrenage du flyout / clic droit → *Paramètres…* | Écran de paramètres |
| Clic droit | Actualiser, Paramètres, **Historique de la batterie…**, Langue, notifications, Quitter |

**Couleurs de marque** (*Paramètres → Affichage*, activées par défaut) : dans le flyout, un appareil dont la marque est reconnue
d'après son nom (Razer, Logitech, SteelSeries, Corsair, HyperX, ASUS ROG, PlayStation, Xbox, Samsung, Apple, Jabra, JBL, Cooler Master,
Steam, Glorious, Keychron) prend la couleur de sa marque pour la pastille et la barre de niveau ; le vert de charge, l'orange et le
rouge restent prioritaires.

**Historique de la batterie** (clic droit → *Historique de la batterie…*) : courbe par appareil sur 24 h, 7 jours ou 30 jours,
avec dernière lecture, minimum, maximum, variation et nombre de mesures ; possibilité d'effacer l'historique d'un appareil. Seuls
les niveaux **réellement lus** (appareil connecté, éveillé, pourcentage non estimé) sont enregistrés, au plus une mesure toutes les
30 minutes par appareil (sauf changement d'état de charge), pendant 30 jours. Un trou de mesure interrompt la ligne : rien n'est
inventé entre deux mesures éloignées. Fichier local `battery_history.json`, jamais envoyé nulle part.

**Flyout** : une carte arrondie par appareil (liseré fin, plus claire sous la souris) avec une pastille d'icône teintée
de la couleur d'état (couleur d'accentuation, vert en charge, orange ou rouge quand la batterie baisse, gris hors ligne),
le nom, une ligne d'état (autonomie, charge, heure de lecture), le pourcentage en grand et une barre de niveau arrondie.
Coins arrondis et fond Acrylic de Windows 11, thème clair ou sombre suivi, affichage inversé pour l'arabe, taille réglable.

**Couleurs** : le chiffre est blanc (ou noir sur barre claire) ; orange sous 40 %, rouge à 15 % et moins
(réglable). Un appareil **branché / en charge** passe en **vert** : chiffre de l'icône, pourcentage, barre de niveau et
ligne « en charge » du flyout (vert plus foncé sur thème clair) ; le vert l'emporte sur l'orange et le rouge. Un appareil
hors ligne est gris. La barre de niveau et le texte du flyout donnent une information qui ne dépend pas de la couleur.

**Autonomie** (flyout et infobulle) :
- *en charge* quand l'appareil charge ;
- « **reste ≈ 5 h 31 min** » : estimation d'après la vitesse de décharge observée sur cet appareil. La vitesse est la
  pente niveau = f(temps) passant par les **instants où le niveau change** (au moins 3 changements répartis sur au
  moins 30 min). Tant que l'autonomie annoncée du modèle est connue, elle est **mélangée à la mesure** (poids de 6 changements) :
  quelques changements espacés, par exemple pendant que l'appareil est au repos, ne donnent plus une durée irréaliste.
  La durée est un **compte à rebours** : elle diminue entre deux baisses et **ne remonte jamais tant que le niveau baisse**
  (garde-fou `AutonomyRatchet` : au plus la valeur précédente moins le temps écoulé ; remis à zéro par une charge ou une absence de plus d'1 h) ;
  (L'ancienne méthode « points perdus ÷ heures écoulées » faisait monter la durée tant que le niveau ne bougeait pas :
  21 h, 24 h, 28 h… puis retombait à la baisse suivante ; elle a été abandonnée, ainsi que les vitesses qu'elle avait apprises) ;
- « **reste plus de 24 h** » : borne basse, tant que le niveau n'a pas baissé. Si le niveau n'a pas bougé pendant T,
  la vitesse est forcément inférieure à 1 point par T. Elle est **plafonnée à 24 h** et ne grandit plus ensuite ;
- « **… (annoncée)** » : si vous avez saisi l'autonomie annoncée par le fabricant (facultatif) et qu'il n'y a pas encore de mesure ;
- « calcul en cours… » : moins de 15 minutes d'observation.
C'est une **estimation** : elle devient plus juste avec le temps et se remet à zéro à chaque charge.

**Temps de charge** : pour un appareil en charge, « en charge · **plein dans ≈ 1 h 6 min** », d'après la vitesse de
charge observée (apprise d'une charge à l'autre ; rien n'est affiché tant qu'elle est inconnue).

**Appareils hors ligne** : un appareil déconnecté reste affiché **en gris** avec sa dernière valeur et l'heure
de cette lecture (« hors ligne · 12:03 »), pendant 7 jours. Aucune alerte n'est envoyée à partir d'une valeur périmée.
Pour le retirer plus tôt : le masquer dans les paramètres.

**Écouteurs à plusieurs batteries** : si l'appareil expose plusieurs services Batterie Bluetooth LE (gauche, droit,
boîtier), le flyout détaille « gauche 80 % · droite 75 % » et l'icône/les alertes suivent l'écouteur le plus faible.

**Détection immédiate** : le branchement ou le débranchement d'un périphérique (et la sortie de veille) relance une
lecture après 2 s, au lieu d'attendre l'actualisation périodique ; jamais plus d'une lecture toutes les 20 s par ce biais.
Les connexions Bluetooth qui ne changent rien côté Windows ne sont vues qu'à l'actualisation périodique.

**Notifications** : *batterie faible* (20 % par défaut, réglable par appareil) et *charge terminée*
(appareil branché qui atteint 100 %). Une seule notification par passage de seuil, y compris après un redémarrage.

**Tester les notifications** : clic droit sur une icône → *Tester les notifications*, ou en ligne de commande
`BatteryTray.exe --test-notification low|full|test` (batterie faible, charge terminée, message générique ; envoyé à l'instance
déjà lancée, avec les vrais textes dans la langue choisie). Les notifications passent par Windows : en mode
**Ne pas déranger / Concentration** (activé automatiquement pendant un jeu ou une application en plein écran), Windows les
**reçoit mais n'affiche aucune bannière**. Pour être prévenu quand même : désactive ce mode, ou ajoute BatteryTray aux
*notifications prioritaires* (Paramètres → Système → Notifications → Ne pas déranger → Définir les notifications prioritaires).

**Langues (20)** : français, anglais, espagnol, allemand, portugais (Portugal), portugais (Brésil), russe, ukrainien, polonais, hongrois,
suédois, turc, vietnamien, chinois simplifié, chinois traditionnel, japonais, coréen, et — **affichées de droite à gauche** —
arabe tunisien, persan et hébreu. Elles couvrent les mêmes langues que BatteryHub. L'application suit la langue d'affichage de Windows
(portugais du Brésil et du Portugal, chinois simplifié et traditionnel distingués), et le menu clic droit ou les paramètres permettent d'en
choisir une autre ; changement immédiat. Police dédiée pour le chinois, le japonais et le coréen. **Les traductions sont rédigées par une IA
et sont à faire relire par des locuteurs natifs** (fichiers `Loc*.cs` : un texte à corriger = une ligne à modifier).

**Paramètres** : une fenêtre dans le style des **Paramètres de Windows 11**, avec une navigation à gauche en quatre pages,
un réglage par carte arrondie, de vrais interrupteurs, des listes déroulantes et des champs numériques arrondis
(saisie au clavier, flèches, molette), et le thème clair ou sombre de Windows (barre de titre comprise).
Les modifications ne sont appliquées qu'avec **OK**.
- **Général** : langue, fréquence d'actualisation (1, 2, 5, 10 min), démarrage avec Windows ;
- **Affichage** : **couleurs de marque**, **taille du flyout** (petit 85 %, normal 100 %, grand 125 %, très grand 150 %), seuils de couleur ;
- **Notifications** : batterie faible, charge terminée, seuil par défaut ;
- **Appareils** : pour chaque appareil, afficher/masquer, renommer, seuil d'alerte,
  autonomie annoncée, ordre (glisser-déposer des lignes par leur marge, ou boutons Monter/Descendre).
Un appareil qui ne donne aucun niveau est masqué d'office, puis réaffiché automatiquement dès qu'un niveau existe
(un masquage choisi par vous n'est jamais défait). Aucun pourcentage n'est jamais inventé.

**Pictogrammes** : casque, souris, clavier, manette, webcam, micro (autres appareils : forme neutre).

**Redémarrage automatique** : si l'application s'arrête sans que vous ayez choisi *Quitter* (arrêt forcé par la mise à jour
d'un autre logiciel, plantage, processus tué), la tâche planifiée « BatteryTray surveillance » la relance au plus
**10 minutes** plus tard.
- Elle exécute `surveiller.vbs` avec wscript, **sans aucune fenêtre** (rien ne sort un jeu du plein écran), pendant votre
  session, sans droits administrateur, même sur batterie.
- Le menu *Quitter* écrit un fichier-témoin (`arret-voulu.flag`) : la tâche ne relance alors rien ; l'application le supprime
  à son démarrage. Si **Démarrer avec Windows** est désactivé dans les paramètres, la tâche ne relance pas non plus.
- Chaque relance est notée dans `%AppData%\BatteryTray\redemarrages.txt`. La tâche est retirée à la désinstallation.

## Quelle que soit la marque : ce qui est commun et ce qui dépend de l'appareil

**Commun à tout appareil qui donne un niveau** (aucune marque n'est codée en dur) : pourcentage dans la barre, flyout,
couleurs (orange / rouge / **vert en charge** / gris hors ligne), autonomie, temps de charge, graphique, notifications,
renommer / masquer / ordonner, 20 langues, appareils gardés en gris quand ils se déconnectent.

**La lecture du niveau** se fait par des sources, de la plus précise à la plus générale (en cas de doublon, la première gagne) :

| Source | Appareils couverts, **toutes marques** | Charge détectée |
|---|---|---|
| Bluetooth LE (service GATT Batterie) | Souris, claviers, casques, écouteurs, micros, manettes BLE | Oui, si l'appareil expose « Battery Power State » |
| **Batteries Windows** (API officielle) | Périphériques HID qui déclarent la page standard « Battery System » (0x85) : certains claviers, souris, stylets, trackpads, onduleurs. La batterie d'un PC portable est ignorée | Oui |
| Manettes (`Windows.Gaming.Input`) | Xbox et compatibles sans fil | Oui |
| **Balayage des périphériques** (propriété standard `DEVPKEY_Device_BatteryLevel`) | Tout périphérique présent pour lequel Windows a renseigné la batterie : casques, écouteurs, claviers, souris Bluetooth, téléphones… (au plus toutes les 5 min) | Non |
| Modules propres à une marque | Razer, Logitech (voir ci-dessous) | Selon le module |

**Ce qui dépend encore de la marque :** un appareil qui n'expose sa batterie **ni** par ces standards **ni** à Windows
(dongle 2,4 GHz propriétaire : Corsair, SteelSeries, HyperX, manettes PlayStation, micros DJI / RØDE…) ne peut pas être
lu sans un module écrit pour son protocole, qui n'est en général pas public. Je n'en ai ajouté que pour tes appareils
(Razer, Logitech). Sur ce PC, les sources génériques s'exécutent sans erreur (balayage de 269 périphériques en 0,1 s)
mais **aucun appareil de plus n'a été trouvé** : elles n'ont donc pas pu être vérifiées sur du matériel réel.

## Pilotes de marques (portés de BatteryHub)

En plus des modules Razer et Logitech de l'auteur, BatteryTray lit en **lecture seule** (une requête de niveau, aucun réglage écrit)
les appareils sans fil de nombreuses marques à récepteur propriétaire. Ces pilotes sont **portés en C#** à partir de BatteryHub
(krisgfx9, GPL-3.0) : voir *Licence* et `NOTICE.md`. Chaque pilote ne parle qu'aux appareils qu'il reconnaît exactement (VID:PID et forme
de la collection HID) et abandonne si la réponse n'a pas le format attendu.

| Pilote | Appareils |
|---|---|
| **SteelSeries** | Arctis 1 / 7 / 7+ / 9 / Pro / Pro Wireless, Arctis Nova 3P / 5 / 7 / 7X / 7P / Pro Wireless, GameBuds ; souris Aerox 3 / 5 / 9 Wireless (dont Gen 2 et CS2 Dragon Lore), Prime Wireless, Rival 3 Wireless (Gen 2), Rival 650, Rival Pro |
| **Razer BlackShark** | BlackShark V3, V3 Pro (dont Xbox), V3 X, V2 HyperSpeed, V2 Pro (2020 et 2023) ; le Barracuda X n'est pas encore porté |
| **PlayStation** | Manettes DualShock 4, DualSense, DualSense Edge (USB ou Bluetooth), adaptateur sans fil DualShock 4 |
| **LAMZU** | Maya X, Maya, Maya M, Tachi, Inca, Paro, Thorn, Atlantis Mini / OG Champion, Orcus, Mini (câble et récepteurs) |
| **Corsair** | Casques Void / Void Pro / Void Elite / HS70 / HS80 ; appareils « Bragi » (Virtuoso, Void V2, HS80 MAX, souris et claviers Slipstream, Dark Core RGB Pro SE) ; Sabre V2 Pro |
| **HyperX** | Cloud II / II Core / III / III S / Alpha / Alpha 2 / Flight / Flight S / Stinger Core / Core DTS ; souris Pulsefire Haste 2 (Core, Wireless, Mini) |
| **Glorious / Hitscan / G-Wolves / WLMouse** | Model O / D / O- / D- / O 2 / D 2 PRO Wireless ; Hitscan Hyperlight ; G-Wolves Fenrir, HTS, HSK, HTX, HTR, VUK, Lycan, WARG… ; WLMouse YING 8K |
| **Kysona / Pulsar / ATK / VXE / Redragon / HECATE** | Souris Kysona, Pulsar (dont X2 V3 sur 3710), ATK / VXE Dragonfly R1, Redragon M916 Pro 1K / M913 / G49, HECATE, et autres souris du protocole Compx à 17 octets |
| **ASTRO** | Casques A50 Gen 3 / Gen 4 / A50 X (Gen 5), A20 Wireless (précise « sur la base » ; respecte le verrou d'Astro Command Center) |
| **ASUS ROG** | Claviers Azoth / Falchion / Strix Scope II, souris Chakram / Gladius / Harpe / Keris / Pugio / Spatha / Strix (récepteur, câble ou ROG Omni Receiver), casques Pelta et Delta II, écouteurs Cetra Open (gauche / droit / boîtier) |
| **Jabra** | Casques à protocole GN (Evolve2, Evolve3…) sur câble USB ou récepteur Link ; bases DECT exclues |
| **JBL Quantum** | 350 / 360 / 610 (HIDV1) et 520 / 650 / 850 / 950 et variantes X (HIDV3), sur récepteur ou câble |
| **Cooler Master** | Souris MM712 et MM731 (câble ou récepteur partagé) |
| **Alienware** | Casque AW920H sur récepteur 2,4 GHz |
| **Audeze** | Casques Maxwell et Maxwell 2 (récepteur ou câble USB-C) |
| **Akko** | Souris V9 Dash Pro et Dash V9 Ultra (récepteur 2,4 GHz) |
| **AKG** | N9 Hybrid (récepteur USB-C) |
| **Razer Barracuda Pro** | Casque sur récepteur 2,4 GHz (active un instant le mode distant de Synapse, rien n'est mémorisé) |
| **AJAZZ** | Clavier AK992 (récepteur 2,4 GHz) |
| **CHERRY** | Clavier MX 3.0S Wireless (récepteur ou câble), souris XTRFY (récepteur 1K) |
| **Endgame Gear** | Souris 4K OP1w / XM2w (v1 et v2, récepteur 4K partagé) |
| **ENDORFY** | Casque Viro Wireless (récepteur 2,4 GHz) |
| **EWEADN** | Souris S7 / S9 (récepteur « Gaming 2.4G » ou câble S9 Ultra) |
| **Fantech / Redragon** | Souris Helios Go XD5 (câble ou récepteur), Redragon Invader Pro |

**Ces pilotes n'ont pas été essayés sur du matériel** (aucun de ces appareils n'est branché sur le PC de l'auteur) : leur analyse de
trames est vérifiée par des tests sur des trames synthétiques, et le comportement réel (délais, réponses) reste à confirmer par les
utilisateurs de ces appareils. Un appareil de cette liste qui n'apparaît pas : lancez `BatteryTray.exe --dump` et joignez le rapport
(sans numéro de série) à votre signalement. Les autres familles de BatteryHub (Logitech additionnels, Razer Barracuda X, Switch Pro, Steam Controller, MSI,
Keychron, Endgame, NuPhy, claviers SONiX/AULA/MCHOSE, etc.) ne sont pas encore portées.

## Licence

BatteryTray est distribué sous **GPL-3.0** (fichier `LICENSE`). Les pilotes de marques sont dérivés du code de BatteryHub, lui-même
sous GPL-3.0 : le projet entier l'est donc aussi. Vous pouvez l'utiliser, le modifier et le redistribuer à condition de fournir
le code source de votre version sous la même licence et de conserver `NOTICE.md`. Aucune garantie.

## Appareils pris en charge (modules de l'auteur)

| Appareil | Méthode de lecture | Résultat |
|---|---|---|
| Razer DeathAdder V4 Pro | Rapport de fonctionnalité Razer (classe 0x07, commande 0x80). Protocole rétro-ingénierié par la communauté (OpenRazer), **non documenté par Razer** | ✅ Fonctionne. 100 % = Razer Synapse |
| Logitech PRO X 2 LIGHTSPEED | Rapport 0x51 sur la collection vendeur 0xFFA0. Protocole décrit dans un patch noyau Linux citant HeadsetControl, **non documenté par Logitech** | ✅ Fonctionne. 75 % = G HUB |
| Logitech G502 LIGHTSPEED | HID++ 2.0, feature 0x1001 (tension) | ⚠️ La souris ne donne que sa tension (≈ 4204 mV) : le pourcentage est **estimé** d'après une courbe Li-ion typique (médiane des 5 dernières tensions) et toujours affiché avec « ~ ». Câble branché détecté par l'état de la mesure. Grise quand la souris dort |
| Écouteurs Bluetooth LE à batteries séparées | Plusieurs services GATT Battery 0x180F + descripteur Presentation Format 0x2904 (gauche = 0x000E, droite = 0x000F) | ❔ Code présent, **non testé** ; beaucoup d'écouteurs (AirPods…) n'exposent pas leurs batteries à Windows |
| Micros sans fil Bluetooth | Mêmes sources Bluetooth que ci-dessus ; reconnus par leur nom (micro, mic, DJI Mic, RØDE…), pictogramme micro | ❔ **Non testé** (aucun micro sans fil appairé) |
| Micros à récepteur propriétaire (DJI Mic, RØDE Wireless, Hollyland…) | — | ❌ Protocoles non publics : il faudrait un module par marque |
| Appareils Bluetooth LE (souris, claviers, casques…) | Service GATT Battery 0x180F (API officielle Windows) | ❔ Code présent, **non testé** (aucun appareil appairé) |
| Appareils Bluetooth classiques | Propriété PnP `DEVPKEY_Device_BatteryLevel` | ❔ Code présent, **non testé** ; dépend de l'appareil |
| Manettes sans fil | `Windows.Gaming.Input` | ❔ Code présent, **non testé** |
| Razer Leviathan V2 X, Logitech StreamCam, Fifine, moniteurs | — | Sans batterie |

Les modules Razer et Logitech reposent sur des protocoles non officiels : ils peuvent cesser de fonctionner
après une mise à jour de firmware. Ils n'envoient que des requêtes de **lecture** et n'écrivent aucun réglage.

## Ce qui n'est pas réalisable (et pourquoi)

- **Widget personnalisé dans la barre des tâches** : Windows 11 a supprimé les « deskbands ». Seule la zone de notification est accessible : les pourcentages s'affichent dans les icônes, à côté du menu ^ et de l'indicateur de langue.
- **Logo + nom + pourcentage dans une seule icône** : une icône fait 16×16 px (32×32 en haute densité). Le nom est dans l'infobulle, le détail dans le flyout.
- **Épingler automatiquement les icônes près de l'horloge** : réservé à l'utilisateur par Windows.
- **Pourcentage exact de la G502** : la souris ne donne que sa tension. Convertir une tension en pourcentage dépend de la charge, de la température et du vieillissement : c'est une estimation, affichée avec « ~ » (seul cas où un pourcentage n'est pas lu sur l'appareil).
- **Appareils à dongle propriétaire d'une autre marque** (SteelSeries, Corsair, HyperX…) : aucune API publique ; il faut un module par marque.
- **Mica pur** : le flyout utilise Acrylic (fond DWM) avec un voile pour la lisibilité.
- **Notification « toast » riche** : elles passent par `NotifyIcon.ShowBalloonTip` (API officielle, sans packaging). Elles sont masquées si Windows est en mode « Ne pas déranger ».

## Compiler

Prérequis : SDK .NET 8 (`dotnet --version`).

```powershell
dotnet build BatteryTray\BatteryTray.csproj -c Release   # compile
powershell -ExecutionPolicy Bypass -File build-installer.ps1   # produit dist\BatteryTray-Setup.exe
```

## Tests

```powershell
dotnet test BatteryTray.Tests
```

475 tests automatiques (xUnit), dont des **tests aléatoires et d'attaque** (graine fixe : fichiers corrompus, niveaux absurdes,
noms hostiles, chemins piégés, absence de toute bibliothèque réseau) et des essais de la fenêtre des paramètres dans les
20 langues en thème clair et sombre : estimateur d'autonomie (décharge régulière, borne basse, charge, remontée du
niveau, intervalles ignorés, vitesse mémorisée entre deux sessions), traductions (20 langues, mêmes clés,
paramètres de texte conservés, arabe seul en droite-à-gauche), formats de durée, règles de notification
(une fois par passage de seuil, réarmement, seuil par appareil, persistance après redémarrage), masquage,
noms et ordre des appareils, appareils hors ligne, temps de charge, batteries multiples, calcul du mini-graphique,
cadence des lectures déclenchées par le matériel, réglages (aller-retour JSON, fichier corrompu), tailles du flyout,
plafond de la borne « plus de 24 h », reconnaissance des noms d'appareils et pictogrammes.
Ils couvrent la **logique** ; la lecture des appareils réels se vérifie à la main (comparaison avec
Razer Synapse et Logitech G HUB).

## Organisation du code

```
BatteryTray\            application (WinForms, .NET 8)
  Providers\            une source de batterie par fichier (Bluetooth, manettes, Razer, Logitech, PRO X 2)
  AutonomyEstimator.cs  estimation de l'autonomie (historique local)
  AutonomyRatchet.cs    la durée affichée ne remonte jamais tant que le niveau baisse
  BatteryHistory.cs     historique de 30 jours (enregistrement, tronçons, résumé)
  HistoryForm.cs        visionneuse d'historique (24 h, 7 jours, 30 jours)
  Brands.cs             reconnaissance de la marque d'après le nom, couleurs de marque
  Providers\Hid\        couche HID commune et pilotes de marques portés de BatteryHub (SteelSeries, Corsair, HyperX, Glorious, Kysona…)
  Cli.cs, DemoData.cs   options --list, --dump, --demo et données d'exemple
  DeviceView.cs         masquage, noms, ordre, autonomie annoncée
  ReadingMerge.cs       doublons entre sources, nettoyage des lectures (niveaux invalides, noms)
  Notifier.cs           notifications « faible » et « charge terminée »
  TrayManager.cs        icônes de la zone de notification (clic, double-clic)
  ClickGate.cs          distingue clic simple et double-clic
  FlyoutForm.cs         flyout
  SettingsForm.cs       écran de paramètres (style Windows 11)
  ModernControls.cs     thème, cartes, interrupteur, boutons, navigation
  ModernInputs.cs       liste déroulante et champ numérique façon Windows 11
  Watchdog.cs           redémarrage automatique (tâche planifiée + script)
  SafeFile.cs           écriture « tout ou rien » des fichiers de données
  Loc.cs, Loc.Settings.cs   textes en 20 langues (Loc.More.cs : les 13 ajoutées)
  Installer.cs          installation / désinstallation (même fichier que le programme)
BatteryTray.Tests\      tests xUnit
Prototype\              prototype console de l'étape de faisabilité
build-installer.ps1     construction de l'installateur
```

## Robustesse

- Une lecture qui plante ou qui se bloque (dongle muet, appareil Bluetooth qui ne répond pas) ne fige jamais les mises à jour :
  délais sur toutes les opérations Bluetooth/HID, filet d'exceptions, et une lecture bloquée plus de 2 min est abandonnée.
- Les erreurs imprévues sont écrites dans `journal.txt` au lieu d'afficher la boîte d'erreur de .NET.
- Un fichier de réglages, d'historique ou d'état abîmé ou modifié à la main (listes `null`, valeurs absurdes) est réparé au chargement.
- Les fichiers de données sont écrits « tout ou rien » : un arrêt brutal pendant l'écriture ne les tronque plus.
- Les lectures venues des appareils sont nettoyées : un niveau hors 0–100 (par exemple 255 = « inconnu ») devient « pas de niveau »,
  les noms trop longs ou contenant des caractères de contrôle sont corrigés.
- L'historique d'autonomie n'est pas réécrit à chaque lecture (au plus toutes les 5 min, et à la fermeture).
- Si l'application s'arrête de façon inattendue, elle est relancée automatiquement (voir *Redémarrage automatique*).
- Les icônes gardent un ordre de création fixe pour que Windows conserve leur **épinglage** même si un appareil est
  masqué ou revient plus tard.
- `BatteryTray.exe --settings` ouvre les paramètres de l'instance déjà lancée (pas de seconde copie qui écraserait les réglages).
- Un appareil masqué d'office (aucun niveau) reste « automatique » même après un clic sur OK dans les paramètres :
  il réapparaît seul dès qu'il a un niveau.

## Publier sur GitHub sans information personnelle

1. **Ne publie pas le dossier de compilation** : `bin/`, `obj/`, `publish/`, `dist/` contiennent des chemins de ton PC
   (dont ton nom d'utilisateur Windows). Le fichier `.gitignore` fourni les exclut.
2. **N'ajoute pas `dist\BatteryTray-Setup.exe` au dépôt** (170 Mo, au-dessus de la limite de 100 Mo par fichier de GitHub) :
   publie-le dans **Releases**. L'installateur ne contient ni ton nom, ni ton e-mail, ni le nom de ton ordinateur.
3. **Vérifie avant de publier** : `powershell -ExecutionPolicy Bypass -File verifier-avant-publication.ps1` cherche, dans les
   fichiers qui seraient publiés, ton nom d'utilisateur Windows, le nom de l'ordinateur, ton nom et ton e-mail Git,
   les chemins de ton profil Windows et de ton dossier de données d'application, et les adresses IP.
   **Ton prénom, ton nom et tes e-mails** ne sont écrits dans aucun fichier du projet : mets-les, un par ligne, dans
   `%USERPROFILE%\.batterytray-termes-prives.txt` (hors du dépôt, jamais publié) et le script les cherchera aussi. Il
   indique le fichier concerné et le type d'information, jamais la valeur trouvée.
4. **Adresse e-mail dans les commits** : Git écrit nom et e-mail dans *chaque* commit, publics sur GitHub. Utilise l'adresse
   anonyme fournie par GitHub (*Settings → Emails → Keep my email addresses private*, de la forme
   `123456+pseudo@users.noreply.github.com`) :

   ```powershell
   git config user.name "ton-pseudo"
   git config user.email "123456+ton-pseudo@users.noreply.github.com"
   ```
5. **Ne publie jamais ton coffre Obsidian** (`2d cerveau`) : c'est un dépôt Git distinct, plein de notes personnelles.
   Ce projet est dans son propre dossier, hors du coffre.

## Confidentialité de l'application

- **Aucun accès réseau** : le code n'utilise aucune API réseau (ni HTTP, ni socket, ni DNS), la seule dépendance est la
  bibliothèque locale HidSharp, et l'application en marche n'ouvre aucune connexion (vérifié avec `Get-NetTCPConnection`,
  `Get-NetUDPEndpoint` et `netstat`). Elle ne connaît et n'envoie donc ni ton adresse IP, ni ta position, ni ton nom.
- **Aucune télémétrie**, aucune mise à jour automatique, aucun compte.
- Elle lit uniquement les **batteries des périphériques** (niveau, état de charge, nom du modèle) et les garde sur ton PC.
- Les noms de périphériques et les niveaux sont écrits dans `%AppData%\BatteryTray` (hors du dépôt).
- Les fichiers compilés ne contiennent pas les chemins de ton PC (`PathMap`, pas de symboles de débogage).

## Données et vie privée

Tout reste sur le PC, dans `%AppData%\BatteryTray` : `settings.json` (réglages), `historique.json`
(lectures récentes pour l'autonomie, 48 h au plus), `battery_history.json` (historique de 30 jours),
`autonomie-affichee.json` (dernière durée affichée), `journal.txt`
(journal borné), `redemarrages.txt` (relances automatiques) et `arret-voulu.flag` (présent seulement après *Quitter*).
Aucun appel réseau.

## Limites connues

- Mémoire mesurée : 15 à 20 Mo au repos, environ 39 Mo flyout ou paramètres ouverts (objectif : moins de 50 Mo).
  La stabilité sur plusieurs jours n'est pas encore vérifiée.
- Les notifications sont déclenchées par le code (vérifié dans les réglages et les tests) mais leur affichage à l'écran n'a pas été constaté.
- Options en ligne de commande :
  - `--list [fichier]` lit chaque source une fois et liste les appareils trouvés (ancien nom : `--providers fichier`) ;
  - `--dump [fichier]` décrit les périphériques HID connectés (VID:PID, nom, collections), **sans numéro de série ni chemin de
    périphérique**, pour signaler un appareil non pris en charge ;
  - `--demo` affiche le flyout avec des appareils d'exemple, sans matériel et sans écrire de réglage ;
  - `--preview image.png` (icônes), `--flyout-preview image.png 125`, `--settings-preview image.png dark|light 0-3`,
    `--history-preview image.png dark|light 0-2` (essais visuels).
  Sans nom de fichier, `--list` et `--dump` écrivent dans la console qui a lancé le programme.
- **Appareils pris en charge** : les modules Razer et Logitech de l'auteur sont vérifiés sur du matériel ; les **pilotes de marques portés de
  BatteryHub** ne le sont pas (voir *Pilotes de marques*) ; les sources génériques non plus. BatteryHub couvre beaucoup plus de marques.
- **Langues** : 20 (voir *Langues*), traductions à faire relire par des locuteurs natifs.
- Le **redémarrage automatique** relance l'application au plus 10 min après un arrêt inattendu (pas instantanément) et n'agit que
  pendant votre session. Le cas « *Quitter* = pas de relance » est couvert par des tests sur la logique, mais n'a pas été essayé de bout en bout.
- Exécutable **non signé** : avertissement SmartScreen à l'installation.
