# IPTV Explorer Desktop

![Version](https://img.shields.io/badge/version-1.0.0-6d5dfc)
![Windows](https://img.shields.io/badge/Windows-10%2F11%20x64-0078d4)
![License](https://img.shields.io/badge/license-MIT-22c55e)

**IPTV Explorer Desktop** est une application Windows native pour explorer et lire les catalogues de fournisseurs IPTV compatibles **Xtream Codes** et **Stalker/MAG**.

L'application est conçue autour d'un principe simple : **aucun fournisseur, identifiant, MAC, token ou catalogue n'est préconfiguré**. Toutes les données restent locales sur la machine de l'utilisateur.

> IPTV Explorer ne fournit aucun abonnement, serveur, chaîne, film ou série. Utilisez uniquement des services et contenus auxquels vous êtes autorisé à accéder.

## Fonctionnalités

- Multi-fournisseur Xtream Codes et Stalker/MAG.
- Assistant d'ajout avec détection, test de connexion et sélection des catégories.
- Live, Films et Séries avec pagination et fiches détaillées.
- Recherche locale Films & Séries via index SQLite par fournisseur.
- Construction automatique du premier index après l'ajout d'un fournisseur actif avec des catégories Films/Séries sélectionnées.
- Lecteur natif **libmpv** intégré : Live, VOD et épisodes.
- Sélecteur d'épisodes directement dans le lecteur pour passer d'un épisode au suivant sans fermer la fenêtre.
- Audio MULTi, sous-titres, volume, seek, plein écran et changement de pistes.
- Téléchargement Films et épisodes avec progression, débit, annulation et copie du lien média.
- Secrets protégés par **Windows DPAPI CurrentUser**.
- Aucune URL média credentialisée, MAC, token ou Authorization retourné à l'interface WebView2.
- Interface sombre, responsive et adaptée à Windows 10/11 x64.

## Télécharger

La version stable est publiée dans **GitHub Releases** :

https://github.com/PassionFlix/iptv-explorer-desktop/releases/latest

La release `v1.0.0` fournit :

- `IPTV-Explorer-Setup-1.0.0-x64.exe` — installateur Windows x64.
- `IPTV-Explorer-1.0.0-win-x64.zip` — version portable.
- `SHA256SUMS.txt` — sommes SHA-256 des artefacts.

Le build Release est **self-contained** : le runtime .NET n'a pas besoin d'être installé séparément. Microsoft Edge WebView2 Runtime doit être disponible sur Windows ; il est déjà présent sur la grande majorité des installations Windows 10/11 modernes.

## Premier démarrage

1. Lancez IPTV Explorer.
2. Cliquez sur **Ajouter un fournisseur**.
3. Choisissez Xtream, Stalker/MAG ou la détection automatique.
4. Saisissez les informations fournies par votre service IPTV.
5. Testez la connexion.
6. Sélectionnez les catégories souhaitées.
7. Activez le fournisseur et explorez Live, Films et Séries.
8. Si des catégories Films ou Séries sont sélectionnées, le premier index de recherche est construit automatiquement en arrière-plan.

Aucune configuration n'est incluse dans l'application distribuée.

## Données locales et confidentialité

Les données d'exécution sont stockées sous :

```text
%LOCALAPPDATA%\IPTV Explorer\
```

Principaux emplacements :

```text
data\iptv-explorer.sqlite   configuration non secrète
data\secrets\              secrets chiffrés DPAPI
indexes\                    index de recherche locaux
cache\                      cache local
logs\                       journaux redacted
```

Les mots de passe, MAC et autres secrets fournisseur ne sont pas stockés en clair dans SQLite.

## Architecture

- C# / .NET 10
- WPF
- WebView2 pour l'interface locale
- SQLite
- DPAPI CurrentUser
- libmpv natif via API C directe
- aucun serveur Web local
- aucun `mpv.exe` lancé en processus externe

Voir [`docs/architecture.md`](docs/architecture.md) pour plus de détails.

## Compiler depuis les sources

Pré-requis développeur :

- Windows 10/11 x64
- SDK .NET 10
- Microsoft Edge WebView2 Runtime
- libmpv x64 pour les tests réels du lecteur

```powershell
git clone https://github.com/PassionFlix/iptv-explorer-desktop.git
Set-Location .\iptv-explorer-desktop

dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build
```

Le premier build Windows récupère automatiquement la build libmpv x64 épinglée, contrôle son SHA-256 et vérifie sa présence dans le dossier de sortie. 7-Zip doit être installé ; le script détecte `7z.exe` dans `PATH` et dans les emplacements Windows habituels.

Pour précharger ou actualiser manuellement le runtime libmpv LGPL utilisé par le build et le packaging :

```powershell
.\scripts\Get-LibMpvRuntime.ps1
```

Pour générer le package portable local :

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.0
```

Avec Inno Setup installé, l'option `-BuildInstaller` génère également l'installateur EXE.

## Release et sécurité

La procédure complète est décrite dans [`docs/release-checklist.md`](docs/release-checklist.md).

Avant toute publication publique, le dépôt et son historique doivent être contrôlés afin de confirmer l'absence de credentials, MAC, tokens, bases SQLite, clés privées ou artefacts locaux.

Pour signaler une vulnérabilité, consultez [`SECURITY.md`](SECURITY.md). Pour contribuer, consultez [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Composants tiers

Les paquets Release incluent une build Windows x64 de libmpv identifiée comme LGPL par son fournisseur de build communautaire. Les détails, la source et le hash épinglé sont documentés dans [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) et [`build/libmpv-runtime.json`](build/libmpv-runtime.json).

## Licence

Le code d'IPTV Explorer Desktop est distribué sous licence **MIT**. Voir [`LICENSE`](LICENSE).

Les composants tiers restent soumis à leurs propres licences.
