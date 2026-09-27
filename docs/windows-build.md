# Build et validation Windows 1.0

IPTV Explorer Desktop cible Windows 10/11 x64.

## Pré-requis développeur

- SDK .NET 10 x64
- Microsoft Edge WebView2 Runtime
- 7-Zip pour récupérer automatiquement le runtime libmpv
- Inno Setup 6 uniquement si vous voulez construire l'installateur EXE

## Validation standard

Depuis PowerShell :

```powershell
Set-Location C:\TeamFlix-Test\IPTV-Explorer-Desktop

dotnet --info
dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build --logger "console;verbosity=normal"
```

Le script historique `scripts/Test-WindowsPhase2.ps1` reste disponible pour les validations interactives Windows et peut lancer l'application avec `-Launch`.

## Runtime libmpv

Les binaires natifs ne sont pas stockés dans Git.

Pour récupérer la build x64 LGPL épinglée et vérifier son SHA-256 :

```powershell
.\scripts\Get-LibMpvRuntime.ps1
```

Le fichier principal attendu est :

```text
src\IPTVExplorer.Desktop\native\mpv\libmpv-2.dll
```

Les informations d'origine et de checksum sont dans `build/libmpv-runtime.json`.

## Publication locale

Pour générer le ZIP portable :

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.0
```

Avec Inno Setup 6 :

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.0 -BuildInstaller
```

Le résultat est écrit dans `dist\`.

## GitHub Release

Le workflow `.github/workflows/release.yml` peut être lancé manuellement pour produire les artefacts de test. Lorsqu'un tag `v1.0.0` est poussé, le même workflow :

1. restaure, build et teste la solution ;
2. récupère la build libmpv x64 LGPL épinglée ;
3. vérifie son SHA-256 ;
4. publie l'application en self-contained `win-x64` ;
5. crée le ZIP portable ;
6. compile l'installateur Inno Setup ;
7. calcule `SHA256SUMS.txt` ;
8. crée la GitHub Release.

Ne créez le tag stable qu'après validation complète de [`release-checklist.md`](release-checklist.md).

## Fresh install

Pour simuler une installation neuve sans supprimer les données existantes :

```powershell
$data = Join-Path $env:LOCALAPPDATA 'IPTV Explorer'
$backup = Join-Path $env:LOCALAPPDATA ('IPTV Explorer.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $data) { Move-Item -LiteralPath $data -Destination $backup }
```

Au premier lancement, l'application doit afficher qu'aucun fournisseur n'est configuré et créer uniquement ses dossiers locaux nécessaires.
