# Parité fonctionnelle Web → Desktop

L’inspection a été strictement structurelle : contrats, algorithmes et navigation. Aucune base, configuration privée, URL, credential, cache ou donnée d’index du portail n’a été importé.

| Fonction Web de référence | Équivalent Desktop | État Phase 2 |
|---|---|---|
| Repository de providers | `IProviderRepository` + `ProviderRepository` SQLite | Implémenté |
| Fabrique de clients par type | `IProviderClientFactory` + `ProviderClientFactory` | Implémenté, aucune branche par nom |
| Contrat client commun | `IProviderClient` async et typé | Implémenté |
| Client Xtream | `XtreamProviderClient` (`player_api.php`, catégories, catalogues, détails, épisodes, résolution) | Implémenté et couvert par fixtures |
| Client Stalker | `StalkerProviderClient` (handshake, profile, pagination unitaire, détails multi-page, `create_link`) | Implémenté et couvert par fixtures |
| Mapping JSON | `JsonSupport` | Normalisation catégories, catalogues et détails implémentée |
| Assistant provider | `ProviderOnboardingService` + assistant WebView2 en cinq étapes | Implémenté |
| Détection automatique | Xtream puis chemins Stalker standards bornés | Implémenté, aucun scan |
| Catégories Live/VOD/Séries | `provider_categories` | Implémenté |
| Politiques All/None/Custom | `CategoryPolicy`, persistence et sélection | Implémenté |
| Synchronisation de catégories | ID, puis nom normalisé unique; disparues conservées | Implémenté |
| Diagnostic provider | `ProviderManagementService` + panneau Paramètres | Implémenté sans secret |
| Jobs de reconstruction | `rebuild_jobs` + `IndexRebuildWorker` | Implémenté |
| Reprise après arrêt | `running` → `interrupted` au lancement | Implémenté |
| Index par provider | `AtomicSearchIndex` | Implémenté |
| Recherche provider-aware | `SearchService` Films/Séries et UI paginée | Implémenté |
| Navigation/dashboard | UI locale Accueil/Live/Films/Séries/Recherche/Paramètres | Implémenté |
| Multi-provider | sélecteur actif persistant via `AppSettingsRepository` | Implémenté |
| Édition/désactivation/suppression | `ProviderManagementService` et UI avec confirmations | Implémenté |
| Lecteur | `MediaReference` → résolution C# → `IPlayerService` | Placeholder Phase 2; libmpv Phase 3 |
| Préférences application/player | `AppPreferences` + écran Paramètres | Implémenté; application réelle par libmpv en Phase 3 |
