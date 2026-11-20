# BatteryTray

Affiche le niveau de batterie de vos périphériques sans fil dans la zone de notification de Windows 11,
à côté de l'indicateur de langue : un pictogramme (casque, souris, clavier, manette, webcam) avec le
pourcentage, un flyout détaillé au clic, des notifications et une autonomie estimée.

100 % hors ligne, aucune télémétrie, aucun droit administrateur, aucune modification du système.

## Installation

1. Lancez `dist\BatteryTray-Setup.exe` (double-clic).
2. Le programme est copié dans `%LocalAppData%\Programs\BatteryTray`, se lance avec Windows et crée un
   raccourci dans le menu Démarrer. Désinstallation : **Paramètres → Applications**.
3. **Épinglez les icônes** : Windows 11 range les nouvelles icônes dans le menu **^**. Ouvrez
   *Paramètres → Personnalisation → Barre des tâches → Autres icônes de barre d'état système* et activez
   BatteryTray (ou glissez les icônes du menu ^ vers la barre). Une application ne peut pas le faire à votre place.

Le fichier fait environ 163 Mo (il embarque .NET : rien d'autre à installer). Il n'est pas signé : Windows peut
afficher un avertissement SmartScreen (« Informations complémentaires » → « Exécuter quand même »).

Installation silencieuse : `BatteryTray-Setup.exe /silent`.

## Utilisation

| Action | Effet |
|---|---|
| Survoler une icône | Infobulle : nom, pourcentage, « en charge » ou autonomie, heure de lecture |
| Clic gauche sur une icône | Flyout « Périphériques » (un appareil par ligne, barre de niveau) |
| Clic sur une ligne du flyout | Déplie un **mini-graphique** du niveau depuis la dernière charge (échelle adaptée aux valeurs mesurées) |
| Engrenage du flyout / clic droit → *Paramètres…* | Écran de paramètres |
| Clic droit | Actualiser, Paramètres, Langue, notifications, Quitter |

**Couleurs** : le chiffre est blanc (ou noir sur barre claire) ; orange sous 40 %, rouge à 15 % et moins
(réglable). Un appareil **branché / en charge** passe en **vert** : chiffre de l'icône, pourcentage, barre de niveau et
ligne « en charge » du flyout (vert plus foncé sur thème clair) ; le vert l'emporte sur l'orange et le rouge. Un appareil
hors ligne est gris. La barre de niveau et le texte du flyout donnent une information qui ne dépend pas de la couleur.

**Autonomie** (flyout et infobulle) :
- *en charge* quand l'appareil charge ;
- « **reste ≈ 5 h 31 min** » : estimation d'après la vitesse de décharge observée sur cet appareil. La vitesse est la
  pente niveau = f(temps) passant par les **instants où le niveau change** (au moins 3 changements répartis sur au
  moins 30 min). La durée est un **compte à rebours** : elle diminue entre deux baisses et ne remonte jamais toute seule.
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

**Langues** : français, arabe tunisien (affichage de droite à gauche), anglais, espagnol, portugais (Portugal),
chinois simplifié, russe. Changement immédiat. Les traductions sont à faire relire par des locuteurs natifs.

**Paramètres** : langue, **taille du flyout** (petit 85 %, normal 100 %, grand 125 %, très grand 150 %),
fréquence d'actualisation (1, 2, 5, 10 min), démarrage avec Windows, notifications,
seuil par défaut, seuils de couleur, et pour chaque appareil : afficher/masquer, renommer, seuil d'alerte,
autonomie annoncée, ordre (glisser-déposer des lignes par leur marge, ou boutons Monter/Descendre).
Un appareil qui ne donne aucun niveau est masqué d'office, puis réaffiché automatiquement dès qu'un niveau existe
(un masquage choisi par vous n'est jamais défait). Aucun pourcentage n'est jamais inventé.

**Pictogrammes** : casque, souris, clavier, manette, webcam, micro (autres appareils : forme neutre).

## Quelle que soit la marque : ce qui est commun et ce qui dépend de l'appareil

**Commun à tout appareil qui donne un niveau** (aucune marque n'est codée en dur) : pourcentage dans la barre, flyout,
couleurs (orange / rouge / **vert en charge** / gris hors ligne), autonomie, temps de charge, graphique, notifications,
renommer / masquer / ordonner, 7 langues, appareils gardés en gris quand ils se déconnectent.

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

## Appareils pris en charge

| Appareil | Méthode de lecture | Résultat |
|---|---|---|
| Razer DeathAdder V4 Pro | Rapport de fonctionnalité Razer (classe 0x07, commande 0x80). Protocole rétro-ingénierié par la communauté (OpenRazer), **non documenté par Razer** | ✅ Fonctionne. 100 % = Razer Synapse |
| Logitech PRO X 2 LIGHTSPEED | Rapport 0x51 sur la collection vendeur 0xFFA0. Protocole décrit dans un patch noyau Linux citant HeadsetControl, **non documenté par Logitech** | ✅ Fonctionne. 75 % = G HUB |
| Logitech G502 LIGHTSPEED | HID++ 2.0, feature 0x1001 (tension) | ⚠️ La souris ne donne que sa tension (≈ 4204 mV), **aucun pourcentage** et aucune estimation : appareil masqué par défaut (réaffichable dans les paramètres, il apparaît alors avec « — ») |
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

- **Widget personnalisé dans la barre des tâches** : Windows 11 a supprimé les « deskbands ». Seule la zone de notification est accessible.
- **Logo + nom + pourcentage dans une seule icône** : une icône fait 16×16 px (32×32 en haute densité). Le nom est dans l'infobulle, le détail dans le flyout.
- **Épingler automatiquement les icônes près de l'horloge** : réservé à l'utilisateur par Windows.
- **Pourcentage de la G502** : la souris ne donne que sa tension. Convertir une tension en pourcentage dépend de la charge, de la température et du vieillissement : le projet n'affiche donc aucun pourcentage pour cet appareil (une estimation a été essayée puis retirée).
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

180 tests automatiques (xUnit) : estimateur d'autonomie (décharge régulière, borne basse, charge, remontée du
niveau, intervalles ignorés, vitesse mémorisée entre deux sessions), traductions (7 langues, mêmes clés,
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
  DeviceView.cs         masquage, noms, ordre, autonomie annoncée
  Notifier.cs           notifications « faible » et « charge terminée »
  TrayManager.cs        icônes de la zone de notification
  FlyoutForm.cs         flyout
  SettingsForm.cs       écran de paramètres
  Loc.cs, Loc.Settings.cs   textes en 7 langues
  Installer.cs          installation / désinstallation (même fichier que le programme)
BatteryTray.Tests\      tests xUnit
Prototype\              prototype console de l'étape de faisabilité
build-installer.ps1     construction de l'installateur
```

## Robustesse

- Une lecture qui plante ou qui se bloque (dongle muet, appareil Bluetooth qui ne répond pas) ne fige jamais les mises à jour :
  délais sur toutes les opérations Bluetooth/HID, filet d'exceptions, et une lecture bloquée plus de 2 min est abandonnée.
- Les erreurs imprévues sont écrites dans `journal.txt` au lieu d'afficher la boîte d'erreur de .NET.
- Un fichier de réglages abîmé ou modifié à la main (listes `null`, valeurs absurdes) est réparé au chargement.
- L'historique d'autonomie n'est pas réécrit à chaque lecture (au plus toutes les 5 min, et à la fermeture).
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
(lectures récentes pour l'autonomie, 48 h au plus), `journal.txt` (journal borné). Aucun appel réseau.

## Limites connues

- Mémoire mesurée : 15 à 20 Mo au repos, environ 39 Mo flyout ou paramètres ouverts (objectif : moins de 50 Mo).
  La stabilité sur plusieurs jours n'est pas encore vérifiée.
- Les notifications sont déclenchées par le code (vérifié dans les réglages et les tests) mais leur affichage à l'écran n'a pas été constaté.
- Options de diagnostic : `--preview image.png` (aperçu des icônes), `--flyout-preview image.png 125` (aperçu du flyout à 125 %),
  `--providers rapport.txt` (lit chaque source une fois : durée et appareils trouvés).
