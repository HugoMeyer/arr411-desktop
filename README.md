# Arr411 Desktop

L'application **Windows** d'**Arr411** : qBittorrent, Radarr, Sonarr et Lidarr dans une seule fenêtre, avec **le vrai site C411** affiché par le moteur d'Edge (WebView2), aussi rapide et aussi net que dans ton navigateur.

C'est le même code que l'[application Android](https://github.com/HugoMeyer/arr411-android) et l'[extension Chrome](https://github.com/HugoMeyer/arr411-chrome) : seule la partie native (fenêtre, réseau, fichiers, notifications) est réécrite, en C#. La version pour serveur, à héberger avec Docker, est [arr411-webapp](https://github.com/HugoMeyer/arr411-webapp).

- **Le vrai C411**, pas une image diffusée : l'onglet C411 est le site lui-même, avec les boutons d'envoi vers qBittorrent, Radarr, Sonarr et Lidarr que l'extension ajoute dans la page.
- **Le VPN reste sur ton PC** : C411 n'est joignable qu'avec un VPN, tu le lances sur le PC et l'application passe par sa connexion. Rien à configurer dans Arr411.
- **Tes accès restent chez toi** : adresses, mots de passe et clés API sont chiffrés pour ton compte Windows et ne sont jamais montrés à la page C411.
- **Intégrée à Windows** : liens magnet et fichiers `.torrent` ouverts depuis Windows, glisser-déposer, zone de notification, lancement avec Windows.
- **Un seul fichier**, `Arr411.exe`, qui n'a rien à installer (pas même .NET).

## Installation

Prérequis : Windows 10 ou 11 (64 bits) et le **WebView2 Runtime** de Microsoft, déjà présent sur Windows 11 et sur la plupart des Windows 10 à jour. S'il manque, Arr411 le dit et propose la page de téléchargement.

1. Télécharge [`Arr411.exe`](https://github.com/HugoMeyer/arr411-desktop/releases/latest/download/Arr411.exe) (dernière [release](https://github.com/HugoMeyer/arr411-desktop/releases)), ou construis-le toi-même ([Développement](#développement)).
2. Mets-le dans un dossier à lui : Windows y retient son emplacement (liens magnet, lancement avec Windows), et Arr411 corrige ces réglages si tu le déplaces.
3. Lance `Arr411.exe`. Le programme n'est pas signé : si SmartScreen affiche « Windows a protégé votre PC », clique sur **Informations complémentaires** puis **Exécuter quand même**.

Au premier lancement, Arr411 s'ouvre sur **Réglages** : renseigne l'adresse (et le mot de passe ou la clé API) de ceux de tes services que tu utilises, les autres peuvent rester vides. Ensuite l'application s'ouvre sur l'onglet que tu as choisi dans les réglages (C411 par défaut).

Pour la désinstaller : `Arr411.exe --unregister`, puis supprime `Arr411.exe` et `%LOCALAPPDATA%\Arr411`.

## Utilisation

### Les onglets

La barre d'onglets donne accès à **C411**, **Radarr**, **Sonarr**, **Lidarr** et **qBittorrent**. Son bouton **Menu** ouvre le menu de l'application (réglages, associations, zone de notification, à propos, quitter).

- Clic droit sur un onglet de service : ses réglages.
- Clic sur l'onglet C411 déjà affiché, ou clic droit dessus : recharger la page, accueil C411, copier le lien, réglages.
- Dans une grande fenêtre, la bibliothèque de Radarr, Sonarr et Lidarr s'étale sur autant de colonnes que la place le permet (affiches d'environ 160 px, lignes de la vue en liste côte à côte) au lieu de trois colonnes géantes.

| Raccourci | Action |
| --- | --- |
| `Ctrl+1` à `Ctrl+5` | C411, Radarr, Sonarr, Lidarr, qBittorrent |
| `Ctrl+Tab`, `Ctrl+Maj+Tab`, `Ctrl+Page suivante`, `Ctrl+Page précédente` | Onglet suivant ou précédent |
| `Ctrl+,` | Réglages |
| `F5`, `Ctrl+R` | Recharger la page C411 |
| `Alt+Gauche`, touche « Précédent » du clavier | Retour (historique de C411, page de l'application) |

Les autres raccourcis du navigateur sont désactivés (pas de zoom au clavier, pas d'outils de développement).

### C411

L'onglet affiche le vrai site. Tu t'y connectes toi-même, et ta session est gardée 30 jours (les cookies de session de c411.org reçoivent une date d'expiration). Dans les pages, un bouton **qBittorrent** apparaît à côté de **Télécharger** et au bout de chaque ligne des listes : choix de la catégorie, ajout en pause, ajout du film, de la série ou de l'album dans Radarr, Sonarr ou Lidarr (toujours non surveillé et sans recherche automatique : C411 reste ta seule source), avertissement si le torrent est déjà dans qBittorrent, badges d'avancement. Tout le détail est dans le [README de l'extension](https://github.com/HugoMeyer/arr411-chrome#readme).

### Liens magnet et fichiers .torrent

- **Depuis Windows** : menu **Associer aux liens magnet et fichiers .torrent…**. Arr411 s'enregistre (sous ton compte seulement, sans droits d'administrateur) puis Windows ouvre ses réglages d'applications par défaut : à toi de choisir Arr411. Rien n'est jamais défini par défaut à ta place.
- **Par glisser-déposer** : dépose sur la fenêtre un lien magnet, un lien vers une page de c411.org ou des fichiers `.torrent` (20 au plus, 20 Mo chacun, 40 Mo en tout). Un texte n'est jamais pris pour un chemin de fichier. Si rien n'est utilisable, la fenêtre répond « Rien à ajouter ». Le site C411 garde pour lui les dépôts qu'il gère lui-même.
- Dans les deux cas, la boîte d'ajout de qBittorrent de l'application s'ouvre avec le lien ou les fichiers : rien n'est ajouté sans ta validation.

### Zone de notification et démarrage avec Windows

Par défaut, fermer la fenêtre la masque et Arr411 reste actif dans la zone de notification pour suivre tes téléchargements (notifications Windows quand l'un se termine). Sur Windows 11, l'icône peut se trouver sous la flèche « Afficher les icônes cachées ». Pour quitter pour de bon : menu **Quitter**, clic droit sur l'icône, ou `Arr411.exe --quit`.

Le menu permet de désactiver ce comportement (**Réduire dans la zone de notification à la fermeture**) et d'activer **Lancer avec Windows** : au démarrage de la session, Arr411 se lance sans montrer sa fenêtre (`--background`).

## Ligne de commande

```
Arr411.exe [options] [<lien magnet | fichier.torrent | lien c411.org> ...]
Arr411.exe [options] -- <un lien magnet | un fichier.torrent | un lien c411.org>
```

| Option | Effet |
| --- | --- |
| `--tab <onglet>` | Affiche un onglet : `c411`, `radarr`, `sonarr`, `lidarr`, `qbit` ou `settings` |
| `--background` | Démarre sans montrer la fenêtre |
| `--quit` | Demande à l'instance qui tourne de quitter, et attend qu'elle soit partie |
| `--register` | Enregistre Arr411 pour les liens magnet et les fichiers `.torrent`, sans ouvrir l'application ni choisir l'application par défaut à ta place |
| `--unregister` | Retire ces enregistrements ainsi que le lancement avec Windows, sans ouvrir l'application |

Une seule instance tourne à la fois : un second lancement lui passe sa demande (un lien, un fichier, un onglet) puis se termine. Les arguments inconnus ou trop nombreux (20 éléments, 64 arguments, 8 192 caractères chacun) sont ignorés, jamais exécutés. Windows place `--` avant le lien ou le fichier qu'il transmet : après lui, rien n'est pris pour une option et **seul l'argument qui suit compte**. Un guillemet dans un lien magnet coupe l'argument en deux, et le reste (qui pourrait désigner un fichier) est ignoré.

Codes de sortie : `0` fait, `1` échec (par exemple WebView2 Runtime absent), `3` une instance tourne mais ne répond pas.

## Données et confidentialité

Tout est dans `%LOCALAPPDATA%\Arr411` (ou le dossier désigné par la variable d'environnement `ARR411_DATA`, pour une copie portable ou des essais : les associations Windows ne sont alors jamais touchées) :

| Élément | Contenu |
| --- | --- |
| `config.dat` | Tes réglages, **chiffrés pour ton compte Windows** (DPAPI) : un autre utilisateur du PC, ou une copie du fichier sur un autre PC, ne peut pas les lire |
| `ui.json` | Taille et position de la fenêtre, dernier onglet (rien de sensible) |
| `WebView2\` | Profil du navigateur : cookies et stockage de l'application et de C411 |
| `cache\covers\` | Pochettes et affiches déjà vues |
| `logs\arr411.log` | Journal (1 Mo au plus, ni mot de passe ni clé API) |

Les fichiers que tu télécharges depuis la page C411 (fichiers `.torrent`, images, `.nfo`...) sont enregistrés par le navigateur intégré dans ton dossier Téléchargements, ou dans celui de la variable `ARR411_DOWNLOADS`, avec la marque « téléchargé depuis Internet » de Windows.

## Sécurité

La page C411 est tenue pour **non fiable** : c'est un site extérieur, qui peut changer ou être compromis.

- **Elle ne voit jamais tes accès.** Elle ne peut parler qu'au moteur d'Arr411, par une liste fermée de messages, et sans ta configuration. Les identifiants des services ne sont envoyés qu'à ces services.
- **Elle n'est servie que si tu la regardes.** Une fois l'onglet C411 masqué (autre onglet, fenêtre réduite ou cachée), ses demandes sont refusées après une minute de grâce, qui laisse finir l'envoi en cours. Même à l'écran, elle est limitée à 30 messages et 5 envois de torrent par tranche de 10 secondes.
- **Elle ne peut pas atteindre ton réseau.** Ses requêtes passent par un client qui n'accepte au départ qu'une adresse de c411.org, en https sur le port 443, sans identifiants dans l'adresse. Les redirections (vers un CDN, par exemple) ne mènent qu'à des serveurs publics, vérifiés à la connexion, jamais à ton réseau local, et les cookies de C411 ne sont envoyés qu'à c411.org. 6 requêtes et 16 Mo au plus.
- **Elle n'écrit aucun fichier elle-même.** Ses téléchargements sont enregistrés par le navigateur intégré, comme dans n'importe quel navigateur : quelques types de fichiers seulement, dans le dossier Téléchargements, avec un nom nettoyé, sans jamais écraser un fichier, au plus 8 par tranche de 10 secondes, et avec la marque « téléchargé depuis Internet » de Windows (SmartScreen, mode protégé d'Office...).
- **Les liens qu'elle demande d'ouvrir** sont triés : une page de c411.org s'affiche dans l'onglet C411, un lien magnet ouvre la boîte d'ajout, une page web (http ou https) s'ouvre dans ton navigateur par défaut, tout le reste est refusé.
- **Un lien magnet, un fichier ou un texte reçus** ne sont jamais exécutés : au pire, ils ouvrent la boîte d'ajout.
- **Les instances se parlent** par un canal réservé à ton compte Windows, avec des messages bornés.
- **Les clés API et le mot de passe de qBittorrent** ne sont jamais dans une adresse, un journal ou un message d'erreur, et ne quittent jamais le service auquel ils sont destinés : l'application ne suit une redirection que si elle reste sur le même serveur et le même service (le port ne change que de http vers https), jamais vers une autre machine, un autre port ou du http après du https. Les images des services sont récupérées par l'application, avec les mêmes règles.
- **Ce qu'une page compromise pourrait encore tenter.** Comme dans l'extension Chrome et l'appli Android, la page partage sa fenêtre JavaScript avec les scripts du pont : un script qui s'exécuterait sur c411.org même pourrait refaire les gestes d'un utilisateur de l'extension (lister tes torrents et tes catégories, ajouter un torrent à qBittorrent, un film, une série ou un album à Radarr, Sonarr ou Lidarr) tant que l'onglet est à l'écran, au rythme limité décrit plus haut. Il ne peut ni lire une clé API ou un mot de passe, ni viser une autre adresse que tes services, ni enregistrer autre chose que les quelques types de fichiers cités plus haut. Une vraie isolation (monde JavaScript isolé, confirmation native de chaque ajout) serait un chantier à part.

## Limites connues

- **Le vrai c411.org n'a pas été testé de bout en bout** (il faut un VPN) : les tests automatiques passent par un faux site C411 fidèle à ses pages. Si C411 change sa mise en page, les boutons peuvent demander une mise à jour de l'extension (`arr411-chrome`).
- **Affichage à une échelle autre que 100 %** non testé.
- Le bouton « Retour » de la souris n'est pas pris en compte (le clavier, oui).
- Pas de zoom de page.
- Arr411 ne se lance pas en administrateur : non testé dans ce mode.
- Si le composant web de Windows (WebView2) s'arrête, Arr411 se relance seul sur le même onglet, une fenêtre masquée le reste. S'il s'arrête dans la minute qui suit le démarrage, Arr411 affiche une erreur et se ferme, pour ne pas tourner en boucle. La session C411 gardée en mémoire peut alors être perdue : il faut se reconnecter.
- L'application est en français, comme C411.

## Dépannage

- **« Arr411 a besoin du composant Microsoft Edge WebView2 Runtime »** : installe-le depuis la page proposée.
- **« Arr411 est déjà lancé mais ne répond pas »** : termine `Arr411.exe` dans le Gestionnaire des tâches, puis relance-le.
- **La page C411 ne se charge pas** : vérifie que ton VPN est actif, et que c411.org s'ouvre dans ton navigateur avec ce même VPN.
- **Tes services sont injoignables quand le VPN est actif** : beaucoup de VPN coupent le réseau local. Autorise-le dans le client VPN, ou utilise pour tes services l'adresse accessible depuis Internet.
- **Journaux** : menu **Ouvrir le dossier des journaux**.
- **Repartir de zéro** : quitte Arr411 et supprime `%LOCALAPPDATA%\Arr411` (réglages, session C411 et cache sont effacés).

## Développement

Il faut le **SDK .NET 10** et, à côté de ce dépôt, les deux dépôts dont Arr411 embarque tel quel le JavaScript :

```
Github Repos\
  arr411-desktop\    (ce dépôt)
  arr411-android\    https://github.com/HugoMeyer/arr411-android
  arr411-chrome\     https://github.com/HugoMeyer/arr411-chrome
```

Ailleurs, désigne-les avec les variables `ARR411_ANDROID` et `ARR411_EXTENSION`. Ce sont les versions de ces dépôts au moment de la construction qui sont embarquées.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Task <tâche>
```

| Tâche | Effet |
| --- | --- |
| `build` | Compile la solution |
| `test` | Compile et lance les tests unitaires (plus de 1 200) |
| `e2e` | Lance le vrai `Arr411.exe` (build Debug) et le pilote : nécessite une session Windows ouverte et déverrouillée, et le WebView2 Runtime. **Le clavier, la souris et le premier plan sont pris pendant plusieurs minutes : ne touche à rien** |
| `publish` | `Arr411.exe` autonome en un seul fichier, dans `dist\publish` |
| `zip` (défaut) | `test`, `publish`, puis `dist\Arr411-Desktop-<version>-win-x64.zip` avec son empreinte SHA-256 |

Le workflow [GitHub Actions](.github/workflows/ci.yml) lance les tests et publie `Arr411.exe` à chaque envoi et chaque pull request (le fichier est téléchargeable depuis l'exécution du workflow). Quand tu pousses un tag `vX.Y.Z` égal à la `Version` de `Directory.Build.props`, il crée aussi une release qui contient `Arr411.exe` lui-même (pas de zip), avec `SHA256SUMS.txt`, `LICENSE` et `THIRD_PARTY_NOTICES.md`. Les tests de bout en bout ne tournent pas dans ce workflow : ils ont besoin d'une session interactive.

```
src\Arr411.Core\           logique sans interface : ponts, HTTP, configuration chiffrée, lancement, fichiers
src\Arr411.Desktop\        fenêtre WinForms, trois vues WebView2, zone de notification, scripts d'adaptation (Scripts\*.js)
tests\Arr411.Core.Tests\   tests unitaires (xUnit)
tests\Arr411.E2E\          tests de bout en bout, qui pilotent le vrai programme
scripts\make-icon.ps1      génère l'icône à partir du logo
```

Les trois vues WebView2 partagent un seul navigateur : l'**application web** d'Arr411 (servie depuis les ressources du programme), le **moteur** (page cachée qui exécute la logique de l'extension : badges, notifications, envois) et **C411** (le vrai site, avec les scripts de l'extension). Le code natif relie ces pages à Windows par des ponts à messages : requêtes HTTP vers tes services (sans CORS), fichiers, notifications, presse-papiers.

## Licence

[MIT](LICENSE) © 2026 JoK3RZz. Éléments tiers (Heroicons, SDK WebView2, .NET) : [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
