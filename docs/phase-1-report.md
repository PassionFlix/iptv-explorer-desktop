# Rapport Phase 1 — 0.1.0-dev

1. **Chemin projet** — `/home/niko/projects/iptv-explorer-desktop/`, autonome et hors webroot. Le chemin `/media/appdata/projects/...` n’était pas inscriptible sans mot de passe sudo.
2. **Structure solution** — `src/`, `tests/`, `docs/`, props NuGet centralisées.
3. **Projets .NET** — Desktop, Core, Infrastructure, Providers, Player, Tests.
4. **Architecture** — couches indépendantes, dépendances dirigées vers Core.
5. **UI WebView2** — accueil, six destinations, état vide, assistant en quatre étapes.
6. **Bridge JS/C#** — enveloppes typées `id/method/params` et `id/ok/result/error`.
7. **SQLite** — base principale sous LocalAppData, foreign keys, WAL et timeout.
8. **Migrations** — migration initiale idempotente et table de versions.
9. **ProviderRepository** — liste, lecture, création, sync et politiques catégories.
10. **SecretStore** — DPAPI CurrentUser; store mémoire pour tests.
11. **ProviderFactory** — sélection uniquement sur `ProviderType`.
12. **Xtream** — auth, catégories, catalogues, détails et résolution; ID épisode respecté.
13. **Stalker** — handshake, profil, catégories, pagination et `create_link`; token volatile.
14. **Onboarding** — validation, test borné, détection, sauvegarde désactivée.
15. **Category policies** — All/None/Custom avec recherche UI à compléter.
16. **Background jobs** — worker interne et reprise `interrupted`.
17. **SearchService** — recherche locale normalisée et paginée.
18. **Player abstraction** — contrat complet, implémentation fail-closed Phase 1.
19. **Modèle tracks** — audio, vidéo, sous-titres et métadonnées demandées.
20. **Design libmpv** — HWND, événements, pistes, DLL, licences et tests documentés.
21. **Tests** — cas de release et sécurité demandés, plus persistance de politique.
22. **Fresh install** — création à vide, aucun seed ni provider.
23. **Scan données provider** — aucune donnée Web copiée; scan nominal et scan des domaines source exécutés avant remise, à répéter avant release.
24. **Build Windows** — commandes exactes et procédure fresh-install documentées.
25. **Limitations** — compilation Windows non exécutée sur l’hôte Debian; UI catalogue, diagnostics avancés, mocks HTTP complets et libmpv restent à faire.
26. **Plan Phase 2** — compiler/corriger sur Windows, ajouter fixtures HTTP exhaustives, terminer onboarding catégories et progression, intégrer libmpv x64, appliquer préférences/pistes, durcir le chargement DLL, puis préparer le packaging.
