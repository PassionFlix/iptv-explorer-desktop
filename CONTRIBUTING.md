# Contributing

Merci de votre intérêt pour IPTV Explorer Desktop.

## Principes

- N'ajoutez jamais de credentials, MAC, tokens, URL privées ou dumps fournisseur réels.
- Les fixtures et exemples doivent utiliser des valeurs manifestement fictives, par exemple `example.invalid`.
- Ne stockez pas de bases SQLite, indexes, caches, logs ou secrets dans Git.
- Les changements liés au lecteur ne doivent jamais exposer une URL média sensible au JavaScript ou à une ligne de commande externe.

## Environnement

- Windows 10/11 x64
- SDK .NET 10
- Microsoft Edge WebView2 Runtime

```powershell
dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build
```

Le premier build Windows récupère automatiquement le runtime libmpv x64 épinglé et vérifie son archive. 7-Zip doit être installé. Pour préparer le runtime avant le build ou forcer son actualisation, exécutez :

```powershell
.\scripts\Get-LibMpvRuntime.ps1
```

## Pull requests

Une PR doit rester ciblée et inclure :

- une description claire du problème ;
- la stratégie de correction ;
- les tests ajoutés ou adaptés ;
- la confirmation `dotnet build` / `dotnet test` ;
- la confirmation qu'aucun secret ou binaire local n'a été ajouté.

Avant soumission :

```powershell
git diff --check
git status --short
```

## Changelog et préparation d'une release

Pendant le développement, ajoutez toute modification visible ou importante sous la rubrique appropriée de `[Unreleased]` dans `CHANGELOG.md`.

Avant une release :

1. Choisissez la nouvelle version et mettez à jour la source de version dans `Directory.Build.props`.
2. Déplacez les entrées de `[Unreleased]` sous `## [X.Y.Z] - YYYY-MM-DD`.
3. Laissez une nouvelle section `[Unreleased]` vide, avec les rubriques `Added`, `Changed`, `Fixed` et `Security`, au-dessus de la version publiée.
4. Exécutez `./scripts/Test-ReleaseNotes.ps1`, puis commitez et validez le build complet.
5. Créez le tag `vX.Y.Z` seulement après cette validation. Le workflow vérifie que le tag correspond à `Directory.Build.props` et extrait automatiquement les notes depuis `CHANGELOG.md`.

Le workflow échoue si la section correspondant à la version est absente ou ne contient aucune entrée utile. Il ne modifie jamais le changelog.

## Licence des contributions

En contribuant au projet, vous acceptez que votre contribution soit distribuée sous la licence MIT du dépôt.
