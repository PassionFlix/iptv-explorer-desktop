# IPTV Explorer Desktop

Version de développement `0.2.0-dev` pour Windows 10/11 x64. L’application est un hôte WPF avec une interface locale WebView2, une logique .NET, SQLite local et des secrets protégés par DPAPI. La Phase 2 couvre l’onboarding Xtream/Stalker, les catégories, catalogues et détails, l’index/recherche locale, les diagnostics et les paramètres. Le lecteur natif libmpv reste réservé à la Phase 3.

## Démarrage développeur

Pré-requis : Windows x64, SDK .NET 10, workload Visual Studio « Desktop development with .NET » et runtime WebView2 Evergreen.

```powershell
dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build
dotnet run --project .\src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj -c Release --no-build
```

Voir [docs/windows-build.md](docs/windows-build.md), [docs/windows-validation-checklist.md](docs/windows-validation-checklist.md), [docs/architecture.md](docs/architecture.md) et [docs/phase-2-report.md](docs/phase-2-report.md).

## Règles de sécurité

- les credentials et MAC sont conservés hors SQLite via `ISecretStore` ;
- le store Windows chiffre les fichiers avec DPAPI `CurrentUser` ;
- les tokens Stalker restent uniquement en mémoire ;
- les messages et logs passent par une redaction centrale ;
- aucune URL média ni credential n’est renvoyé à JavaScript ;
- aucun `mpv.exe`, shell ou navigateur externe n’est lancé ;
- les fixtures utilisent exclusivement `example.invalid` et des valeurs manifestement fictives.
