# Build et validation Windows Phase 2

La validation WPF, WebView2 et DPAPI doit être exécutée sur Windows 10/11 x64. Un build croisé Debian ne remplace pas cette validation.

## Préparer la machine

Utiliser exclusivement :

```text
C:\TeamFlix-Test\IPTV-Explorer-Desktop
```

Ne jamais viser `C:\TeamFlix-Test\TeamFlix-Uploader` ni un autre projet. Installer le SDK .NET 10 x64, le workload Visual Studio **Desktop development with .NET** et le runtime WebView2 Evergreen.

Pour une copie par WSL, depuis la racine Debian du projet :

```bash
./scripts/export-source.sh /mnt/c/TeamFlix-Test/IPTV-Explorer-Desktop
```

Le script exclut `.git`, `bin`, `obj`, `.vs`, bases SQLite et journaux associés, cache, indexes, logs, secrets et données utilisateur. Il ne supprime aucun fichier de la destination. Pour une machine distante, produire d’abord un répertoire neutre local, puis le transférer avec l’outil approuvé par l’équipe.

## Commandes Release exactes

Depuis PowerShell x64 :

```powershell
Set-Location C:\TeamFlix-Test\IPTV-Explorer-Desktop
dotnet --info
dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build --logger "console;verbosity=normal"
dotnet run --project .\src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj -c Release --no-build
```

Le script `scripts/Test-WindowsPhase2.ps1` automatise les quatre premières commandes et peut lancer l’application avec `-Launch`.

## Fresh install récupérable

Fermer l’application, puis déplacer les données existantes au lieu de les supprimer :

```powershell
$data = Join-Path $env:LOCALAPPDATA 'IPTV Explorer'
$backup = Join-Path $env:LOCALAPPDATA ('IPTV Explorer.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $data) { Move-Item -LiteralPath $data -Destination $backup }
dotnet run --project .\src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj -c Release --no-build
```

Attendu : écran « Aucun fournisseur IPTV configuré » et bouton « Ajouter un fournisseur ». Le runtime crée seulement `%LOCALAPPDATA%\IPTV Explorer\data`, `cache`, `indexes`, `logs` et `data\secrets`.

## DPAPI et redaction

Créer manuellement un provider fictif, fermer et relancer l’application, puis confirmer qu’il est retrouvé. Inspecter `data\iptv-explorer.sqlite` et `logs\desktop-*.log` : aucun username, password, MAC, token, header Authorization ni URL média credentialisée ne doit apparaître. Les blobs `data\secrets\*.bin` doivent être illisibles en clair et uniquement déchiffrables par le même compte Windows (`CurrentUser`).

La checklist détaillée est dans [windows-validation-checklist.md](windows-validation-checklist.md). Aucun installer n’est produit en Phase 2.
