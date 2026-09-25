# Architecture Phase 2

## Frontières

```text
WPF MainWindow
  └─ WebView2 (HTML/CSS/JS locaux, CSP)
       ⇅ postMessage / WebMessageReceived
BridgeRouter
  ├─ ProviderOnboardingService
  ├─ IProviderRepository ── SQLite principal
  ├─ IProviderClientFactory ── Xtream / Stalker ── HttpClientFactory
  ├─ ISearchService ── index SQLite par provider
  └─ PlaybackCoordinator ── IPlayerService ── implémentation libmpv Phase 3
```

Il n’existe ni serveur localhost, ni PHP, ni Kestrel. `appassets.local` est un nom virtuel WebView2 mappé vers le dossier `ui` embarqué. Toute navigation vers un autre hôte est annulée.

## Données locales

`AppPaths` est l’unique source de chemins :

```text
%LOCALAPPDATA%\IPTV Explorer\
  data\iptv-explorer.sqlite
  data\secrets\<référence>.bin
  cache\
  indexes\search-<provider-key>.sqlite
  logs\desktop-YYYYMMDD.log
```

L’application ne demande pas de droits administrateur et n’écrit jamais dans `Program Files`.

## Secrets et journalisation

`providers.secret_reference` pointe vers un blob DPAPI lié à l’utilisateur Windows courant. Le blob contient les credentials sérialisés, chiffrés avant écriture. `InMemorySecretStore` remplace DPAPI dans les tests. Le token de session Stalker est un champ privé en mémoire du client et ne traverse ni repository, ni bridge.

`LogRedactor` masque les paramètres sensibles, Bearer, motifs MAC, userinfo et chemins média Xtream credentialisés. Le client HTTP n’inclut aucune instrumentation métier qui journalise une URI média. Les brouillons d’onboarding restent en mémoire et le bridge ne renvoie ni secret, ni référence de secret, ni token.

## Index et jobs

Le worker `IndexRebuildWorker` est hébergé dans le même processus. Les jobs sont persistés avec les états `queued`, `running`, `completed`, `failed`, `interrupted`. Au démarrage, les jobs restés `running` sont marqués `interrupted`. Seules les catégories VOD/Séries sélectionnées et non techniques sont indexées. Un index est construit dans `search-<key>.sqlite.tmp`, vérifié par `PRAGMA quick_check`, puis remplacé atomiquement ; l’ancien fichier reste en service jusqu’au remplacement. La clé primaire `(catalog_type, remote_id)` assure la déduplication provider-aware.

## Frontière de lecture

La WebView transmet uniquement un `MediaReference` opaque (`providerKey`, type, identifiant média/épisode et extension). `PlaybackCoordinator` valide la référence, demande au client provider de résoudre l’URL dans le processus C#, puis appelle `IPlayerService`. L’implémentation Phase 2 est un placeholder; le contrat est prêt pour libmpv sans changement de l’UI.
