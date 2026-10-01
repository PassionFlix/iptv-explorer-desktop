# Xtream : catalogue local et protection anti-ban

## Périmètre et interdiction des tests réels

Ne jamais tester ni diagnostiquer automatiquement le fournisseur IPTV réel. Toute validation utilise des handlers HTTP en mémoire, des fixtures anonymisées, des bases SQLite temporaires et un DOM simulé. Ne pas lancer l'application configurée ni les anciens helpers de diagnostic contre le fournisseur.

Cette architecture s'applique à Xtream. Stalker/MAG conserve son protocole, sa session et sa pagination existants : aucun bulk spéculatif ni nouveau polling n'est ajouté. Le lecteur/libmpv, fullscreen, reprise, téléchargements, carrousels et CSS du backdrop restent inchangés.

## Une génération atomique, trois catalogues

`CatalogSnapshotRepository` stocke dans la base de l'application :

- `catalog_snapshots` : clé fournisseur, date du dernier succès complet et numéro de génération ;
- `catalog_items` : lignes par fournisseur/type/id, catégorie et titre indexés, puis un document **typé et assaini**, jamais le JSON source ;
- `media_details` : détails persistants par fournisseur/type/id, sans expiration automatique.

La transaction remplace simultanément Live, VOD et Series, la date et la génération. Une erreur HTTP, JSON, validation ou écriture SQLite conserve l'ancien snapshot complet. Les lecteurs voient une génération publiée, jamais un catalogue partiellement remplacé. Les détails persistent indépendamment des refreshs et de l'index de recherche. La suppression d'un fournisseur cascade sur ces données.

La seule connexion de publication bulk utilise un cache SQLite privé, sans pooling, sur la base WAL existante : les lecteurs UI peuvent continuer à lire la génération publiée pendant la transaction volumineuse. Les connexions ordinaires et les pools de l'index ne sont pas modifiés. Une régression locale lit le snapshot pendant son remplacement et vérifie qu'aucune génération partielle n'est visible.

`CatalogSanitizer` conserve une liste explicite de champs utiles, retire `Metadata` brut, valide ids/extensions, assainit les textes et les images avec les secrets en mémoire. Aucun cmd, token, credential ou lien de lecture n'est extrait du JSON pour être persisté. `MediaArtwork` et `HomeArtwork` strict restent inchangés. DPAPI reste le stockage des secrets.

## Démarrage et changement de fournisseur

L'UI rend les données locales avant d'appeler `catalog.session` sans attendre son résultat pour ouvrir l'application. `CatalogRefreshService` mémorise la tentative par fournisseur pendant toute sa durée de vie, y compris après un échec. Revenir sur le même fournisseur n'en déclenche pas une autre.

`StartupCooldown = TimeSpan.FromMinutes(30)` est centralisé et testé avec `TimeProvider`. Si le dernier snapshot réussi a moins de 30 minutes, même après redémarrage, aucun appel catalogue n'est fait. À partir de 30 minutes ou en l'absence de snapshot :

1. `get_live_streams`, sans `category_id` ;
2. puis `get_vod_streams`, sans `category_id` ;
3. puis `get_series`, sans `category_id`.

Un succès fait exactement trois appels physiques. Une erreur arrête la séquence : un à trois appels au maximum, aucun retry. Les réponses ne sont publiées qu'après les trois succès. L'UI indique discrètement que les données locales sont conservées en cas d'échec. Aucun appel de compte/catégories/détail ne complète ce démarrage.

`XtreamProviderClient` utilise un seul `HttpClient.SendAsync` par opération, sans `HttpRetry`, sans redirection automatique dans le handler de production. Le sémaphore partagé par la factory sérialise les requêtes Xtream, lecture de leur réponse comprise. 403, 429, 5xx/520, timeout, réseau et JSON invalide n'entraînent aucune nouvelle tentative. `HttpRetry` et la réauthentification Stalker ne sont pas modifiés.

## Lectures locales et index

`IProviderClientFactory` fournit `LocalProviderClientFactory` aux écrans, au lecteur et aux actions média. Pour Xtream, il ne peut pas récupérer de métadonnées distantes : Live filtre localement, Films/Séries paginent avec SQLite, les détails du lecteur viennent du cache. Les anciennes méthodes par catégorie et d'hydratation du client Xtream distant refusent les appels avant tout HTTP. Seules les opérations explicites reçoivent `IRemoteProviderClientFactory`.

Les noms et sélections de catégories existants sont conservés. Les ids inconnus du snapshot deviennent des catégories locales de repli, sans appel API et en respectant la politique all/none/custom. La navigation ne dépend donc pas d'une synchronisation des noms.

Pour Xtream, `IndexRebuildWorker` lit uniquement `SearchDocumentsAsync` dans le snapshot. VOD et Series sont lus par une même requête SQLite, avec la sélection locale. Aucun client fournisseur n'est créé dans ce chemin. Un snapshot absent ne détruit pas l'ancien index. Un succès bulk met une reconstruction locale en file ; si une génération change pendant la construction, une nouvelle passe locale est mise en file. `SearchIndexFileAccess`, le `ClearPool` ciblé et le remplacement atomique Windows sont inchangés.

Le polling UI de progression ne lit que `rebuild_jobs`, jamais le fournisseur. Après publication de l'index, Home/recherche sont rafraîchis localement. Les réponses devenues obsolètes après un changement de fournisseur sont ignorées.

## Fiches, artwork et double clic

`BridgeRouter.VodDetail` et `SeriesDetail` dirigent les clics Xtream vers `MediaDetailService` :

- film : un détail persistant existant est prioritaire ; sinon un synopsis et une extension présents dans le bulk suffisent à rendre/lire la fiche. Les autres champs sont facultatifs. Si ces données manquent, au plus un `get_vod_info`, puis persistance ;
- série : au plus un `get_series_info` à la première fiche non cachée, puis persistance du poster, synopsis, metadata, saisons et épisodes (ids, titres, numéros, extensions). Toute ouverture suivante lit le cache, sans TTL ;
- un échec ne lance pas de retry. Une nouvelle action explicite ultérieure peut retenter un détail non caché ;
- `SingleFlight` partage la tâche en vol par fournisseur/type/id, succès **ou échec**. Annuler une attente UI n'annule pas la requête partagée pour en lancer une seconde ;
- les refreshs manuels du catalogue, catégories et contrôles de compte sont aussi dédupliqués pendant leur exécution.

`series_artwork` est alimenté par le snapshot (entries absentes/nulles) et par `MediaDetailService.SeriesAsync` après le détail demandé. Un poster détail connu n'est pas écrasé par le snapshot. L'accueil ne consulte que l'index et ce cache. Un `img.onerror` affiche le placeholder, sans RPC ni réparation. Le lecteur obtient ses épisodes de `media_details` sans modifier son code ni créer une requête cachée ; si un ancien historique n'a aucun détail local, la lecture reste possible mais la liste d'épisodes attend une ouverture explicite de la fiche.

## Paramètres et matrice des appels

« Actualiser le catalogue » dans Paramètres → Index ignore volontairement le cooldown, garde la séquence de trois appels et déclenche ensuite l'indexation locale. Le bouton est désactivé pendant l'opération ; le backend déduplique également. La date/heure de la dernière actualisation réussie est affichée sans URL fournisseur ni secret.

Après clarification utilisateur, « Tester la connexion » à l'ajout et « Diagnostic » sont autorisés à effectuer **un contrôle de compte manuel**, sans retry et sans synchronisation implicite des catégories. Enregistrer/modifier/activer Xtream ne teste pas automatiquement le compte. La synchronisation des catégories reste une action explicite séparée, séquentielle.

Les nombres ci-dessous comptent l'API de métadonnées/catalogue Xtream, pas les flux de lecture ni le chargement normal des images déjà référencées. Aucun appel API n'est fait pour réparer ces images.

| Action UI | Requêtes API Xtream |
| --- | ---: |
| Démarrage, snapshot réussi < 30 min | 0 |
| Démarrage sans snapshot ou âge ≥ 30 min | 3 si succès, 3 maximum |
| Retour au même fournisseur pendant la session | 0 |
| Nouveau fournisseur actif | 0 ou 3 maximum, mêmes règles |
| Accueil / dashboard / récents / continuer à regarder | 0 |
| Navigation Live, Films, Séries / pagination / catégorie | 0 |
| Recherche | 0 |
| Reconstruire l'index Xtream | 0 |
| Poster cassé / réparation artwork | 0 |
| Film déjà caché ou bulk suffisant | 0 |
| Première fiche film avec données nécessaires manquantes | 1 maximum |
| Première fiche série non cachée | 1 maximum |
| Fiche série déjà cachée | 0 |
| Double clic sur la même fiche en vol | 1 partagé maximum |
| Synchronisation catégories manuelle | 3 maximum, séquentiels |
| Actualiser le catalogue manuellement | 3 si succès, 3 maximum |
| Tester la connexion / Diagnostic manuels | 1 maximum |
| Enregistrer / modifier / activer Xtream | 0 pour l'opération ; activation d'un nouveau fournisseur actif applique ensuite la règle de session |

## Validation locale

Les régressions comptent les requêtes au niveau du `HttpMessageHandler` : ordre, absence de `category_id`, concurrence maximale de un, limites de tentatives, cooldown persistant, navigation/page/recherche/Home/index locaux, détail/épisodes persistants, double clic/annulation, isolation, rollback SQLite, génération concurrente, secrets et artwork. Les tests Stalker existants restent exécutés.

```text
dotnet restore
dotnet build IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test IPTVExplorer.Desktop.sln -c Release --no-build
node --test tests/ui/home-artwork.test.cjs tests/ui/catalog-cache.test.cjs
git diff --check
```

Les tests DOM exécutent les fonctions de production avec un bridge et des éléments DOM simulés, sans navigateur ni réseau. La CI Windows exécute les deux suites. Aucun résultat ne constitue une validation sur le fournisseur réel.

## Fichiers de cette évolution

- Core : `Contracts.cs`, `Domain.cs`, `SingleFlight.cs` (nouveau).
- Infrastructure : `Database.cs`, `RebuildJobs.cs`, et les nouveaux `CatalogSnapshotRepository.cs`, `CatalogSanitizer.cs`, `CatalogRefreshService.cs`, `MediaDetailService.cs`, `LocalProviderClientFactory.cs`.
- Providers : `XtreamProviderClient.cs`, `ProviderClientFactory.cs`, `ProviderManagementService.cs`, `ProviderOnboardingService.cs`, `JsonSupport.cs`.
- Desktop : `App.xaml.cs`, `BridgeRouter.cs`, `ui/app.js`, `ui/index.html`.
- Tests .NET : `AntiBanCatalogTests.cs` (nouveau), `HomeMediaTests.cs`, `ProviderFixtureTests.cs`, `SeriesArtworkTests.cs`.
- Tests DOM : `tests/ui/catalog-cache.test.cjs` (nouveau) ; la suite `home-artwork.test.cjs` reste inchangée et exécutée.
- CI : `.github/workflows/ci.yml`.
- Documentation : `docs/xtream-local-catalog.md` (nouveau), `docs/home-media.md`.
