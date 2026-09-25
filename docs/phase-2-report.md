# Rapport Phase 2 — IPTV Explorer Desktop

Date de préparation Debian : 2026-09-25. Version : `0.2.0-dev`.

Ce rapport distingue **implémenté/automatisé sur Debian** de **validation Windows en attente**. Aucun accès Windows 10/11 x64 ni provider utilisateur n’était disponible pendant cette intervention; aucune validation WPF, WebView2, DPAPI CurrentUser ou réseau réel n’est donc revendiquée.

1. **Build Windows** — Build croisé `net10.0-windows` Debug et Release réussi, 0 avertissement/0 erreur. Build natif Windows à exécuter avec `scripts/Test-WindowsPhase2.ps1`.
2. **Lancement WPF** — Hôte et démarrage configurés; lancement réel Windows en attente.
3. **WebView2** — UI locale, mapping virtuel, CSP/navigation et bridge allowlist en place; validation runtime Windows en attente.
4. **Fresh install** — Test automatisé SQLite/dossiers sans provider ni index/secret; fresh install `%LOCALAPPDATA%` réel en attente.
5. **Onboarding UI** — Assistant cinq étapes : identité/type/URL, credentials conditionnels, test, catégories, activation explicite.
6. **DPAPI Windows réel** — `ProtectedData`, portée `CurrentUser`, écritures temporaires puis remplacement. Inspection réelle Windows en attente.
7. **Xtream** — Auth, catégories, Live, VOD, séries, détails, vrais IDs d’épisodes et résolution C# couverts par fixtures.
8. **Stalker** — Handshake/profile, catégories, catalogues, pagination, détails multi-page, `create_link` et réauthentification couverts par fixtures.
9. **Catégories** — Persistées par provider/catalogue; catégories techniques filtrées; disparues conservées avec revue requise.
10. **All/None/Custom** — Persistance, compteurs, filtre et conservation des sélections masquées implémentés et testés.
11. **Gros catalogue** — Fixture 500 Live/200 VOD/100 Séries; rendu UI borné à 200 lignes simultanées et état complet conservé hors DOM.
12. **Synchronisation** — ID distant, puis nom normalisé unique; règle de sélection des nouvelles catégories testée pour les trois modes.
13. **Dashboard** — Provider, type, statut/expiration locale, catégories et état d’index; panne provider non bloquante.
14. **Provider switch** — Sélecteur des providers activés et clé active persistée dans `app_settings`.
15. **Live catalogue** — Catégories sélectionnées, logos lazy, placeholder et action Lire.
16. **VOD catalogue** — Grille, année/rating et pagination; appels asynchrones annulables.
17. **Fiche VOD** — Poster, synopsis, métadonnées et action Lire via référence opaque.
18. **Series catalogue** — Grille et pagination pour Xtream/Stalker.
19. **Fiche Série** — Métadonnées, saisons et épisodes normalisés; ID épisode Xtream conservé.
20. **Pagination Stalker** — Une seule page catalogue par appel; fixture avec total 2 684 vérifie que seule la page demandée est chargée.
21. **Index worker** — Worker hébergé, file persistée, catégories sélectionnées VOD/Séries, exclusion des agrégats techniques.
22. **Progression** — Statut, libellé d’étape et compteurs transmis par polling sans bloquer l’UI.
23. **Interruption/recovery** — Jobs `running` convertis en `interrupted` au redémarrage; test automatisé.
24. **Atomicité** — Construction `.tmp`, `PRAGMA quick_check`, remplacement atomique; test d’échec confirme que l’ancien index reste lisible.
25. **Dirty state** — Une modification de policy marque l’index dirty; une reconstruction réussie nettoie VOD/Séries; test automatisé.
26. **Recherche** — Locale, provider-aware, minimum trois caractères, Films/Séries et pagination indépendante.
27. **Diagnostics** — Type, host, endpoint API, compte, catégories et latence, sans secret.
28. **Édition provider** — Nom/URL/credentials, champs secrets vides conservés, nouvelle configuration testée avant permutation atomique des références.
29. **Désactivation** — Confirmation UI, re-test avant activation, retrait du sélecteur principal si désactivé.
30. **Player placeholder** — Fenêtre locale, appel complet de `IPlayerService`, aucun navigateur externe.
31. **MediaReference** — Modèle opaque et validation C#; résolution Xtream/Stalker exclusivement côté C#.
32. **Logs/redaction** — Masquage query, Bearer, MAC, userinfo et chemins média credentialisés; fixtures sans données privées.
33. **Tests fixtures** — 29 tests réussis sur Debian, couvrant fresh install, providers, catégories, sécurité, reprise, index, préférences et cycle de vie des secrets.
34. **Tests réels** — Non effectués : aucun Windows/provider utilisateur disponible. Checklist prête, sans inclure de secret.
35. **Performances** — Suite automatisée en environ 0,2 s sur cette machine; fixture gros catalogue sous la limite testée de 5 s. Mesures UI/WebView2 réelles en attente.
36. **Scan données privées** — Sources/artefacts à scanner avant transfert; seules les valeurs fictives `example.invalid`, `fixture-*` et adresses MAC nulles sont autorisées.
37. **Fichiers créés/modifiés** — Core (domain/contrats), Infrastructure (schéma/repositories/index/settings), Providers (Xtream/Stalker/onboarding/gestion), Desktop (bridge/playback/UI), tests, scripts de transfert/validation et documentation.
38. **Limitations** — Pas de validation Windows réelle, pas de provider réseau réel, pas de cache image dédié, pas de téléchargement, pas de packaging et pas de libmpv.
39. **Plan exact Phase 3 libmpv** — Ajouter les binaires x64 et leur attribution; implémenter `LibMpvPlayerService` derrière `IPlayerService`; héberger la surface vidéo native; mapper audio/sous-titres/forced/HDR; appliquer `AppPreferences`; ajouter play/pause/seek/volume/plein écran; persister reprise/historique; gérer erreurs/reconnexion; tester URLs Xtream/Stalker sans les exposer; tests Windows x64, fermeture propre et scan final; seulement ensuite préparer le packaging.

## Validation automatisée Debian

```text
dotnet build IPTVExplorer.Desktop.sln -c Release --no-restore
0 Warning(s), 0 Error(s)

dotnet test IPTVExplorer.Desktop.sln -c Release --no-build --no-restore
29 passed, 0 failed, 0 skipped

node --check src/IPTVExplorer.Desktop/ui/app.js
OK
```

Le transfert neutre et les validations Windows sont décrits dans [windows-build.md](windows-build.md) et [windows-validation-checklist.md](windows-validation-checklist.md). Aucun commit, push ou installer n’a été produit.
