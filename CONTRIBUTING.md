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

Pour les tests réels du lecteur, placez un runtime libmpv x64 dans `src/IPTVExplorer.Desktop/native/mpv/` ou exécutez :

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

## Licence des contributions

En contribuant au projet, vous acceptez que votre contribution soit distribuée sous la licence MIT du dépôt.
