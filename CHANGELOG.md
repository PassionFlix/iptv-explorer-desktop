# Changelog

Toutes les modifications importantes d’IPTV Explorer sont documentées ici.

Ce document s’inspire de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le versionnement sémantique.

## [Unreleased]

### Added

### Changed

### Fixed

- Amélioration de la compatibilité avec certains portails Stalker/MAG nécessitant un profil de requête MAG complet.
- Correction de la récupération des informations de compte et de la date d'expiration sur certains fournisseurs Stalker/MAG.
- Correction de l’extension proposée lors du téléchargement de médias Stalker dont l’URL de lecture passe par un script PHP.

### Security

## [1.1.0] - 2026-09-30

### Added

- Nouvel accueil avec raccourcis, état local du fournisseur et informations d’index plus lisibles.
- Reprise de lecture avec une section « Continuer à regarder » et mémorisation locale de la progression.
- Carrousels des Films et Séries récemment ajoutés.
- Fond d’accueil dynamique issu des médias disponibles, avec transitions douces et respect de la réduction des animations.
- Contrôles superposés et masquage automatique dans le lecteur en plein écran.
- Détection des nouvelles versions stables publiées sur GitHub.
- Bouton « Actualiser le catalogue » avec affichage de la date de dernière actualisation.
- Snapshots locaux séparés par fournisseur pour les catalogues Xtream Live, Films et Séries.
- Cache persistant des fiches Films et Séries, y compris les saisons et épisodes.
- Cache local des affiches de séries lorsqu’une image sûre est fournie par le catalogue ou une fiche ouverte par l’utilisateur.

### Changed

- Barre latérale fixe et accueil adapté aux différentes tailles de fenêtre.
- Plein écran amélioré sur les configurations multi-écrans et les écrans avec mise à l’échelle DPI.
- Navigation Xtream désormais principalement locale : Live, Films, Séries, pagination, recherche et contenus récents utilisent les snapshots enregistrés.
- Actualisation Xtream limitée à trois appels catalogue maximum, exécutés successivement, avec un délai de 30 minutes après un succès.
- Reconstruction de l’index local Xtream effectuée uniquement à partir du snapshot local.
- Fiches de séries chargées depuis le fournisseur uniquement après une action explicite, puis réutilisées depuis le cache.
- Appels automatiques au fournisseur fortement réduits ; aucune réparation réseau automatique des affiches ou arrière-plans.
- Comportement de Lecture et Stop rendu plus prévisible dans le lecteur natif.
- Compatibilité de migration SQLite depuis la base et l’index publics 1.0.0 davantage couverte et vérifiée.

### Fixed

- Le thème « Système » suit désormais correctement le thème clair ou sombre de Windows.
- Clarification de l’étape de connexion Xtream lors de l’ajout d’un fournisseur.
- Sécurisation du runtime libmpv lors des builds et packages de release grâce à la vérification des empreintes de l’archive et de la DLL.
- Nettoyage correct des téléchargements de Films et d’épisodes à leur fin ou lors de la fermeture de l’application.
- Protection étendue des informations sensibles et des signatures d’URL dans les journaux.
- Correction de la préparation des futures versions et de l’affichage de version.
- Sécurisation de la suppression des fournisseurs et de leurs données locales.
- Amélioration de la disposition et du suivi des téléchargements d’épisodes.
- Les affiches verticales utilisées comme fond d’accueil conservent désormais leur cadrage dans une composition sombre dédiée, sans zoom ni appel fournisseur supplémentaire.
- Reprise appliquée au bon timecode après le chargement effectif du média (`FILE_LOADED`).
- Lecture de nouveau possible après l’utilisation de Stop.
- Verrous et handles SQLite libérés correctement avant le remplacement atomique d’un index sous Windows.
- Runtime libmpv correctement disponible lors d’un build propre et vérifié pendant la préparation des packages.
- Plusieurs problèmes de placement, redimensionnement et contrôles en plein écran.
- Affiches des contenus récents mieux réutilisées depuis le cache local, avec placeholder en cas d’image indisponible.
- Divers problèmes de disposition, superposition, navigation et interaction sur l’accueil et ses carrousels.

### Security

- Aucune donnée d’authentification fournisseur n’est enregistrée dans les snapshots ou caches SQLite.
- Protection DPAPI des secrets fournisseur conservée.
- Validation renforcée des URL d’affiches et d’arrière-plans avant stockage ou affichage.
- Aucune URL média contenant des identifiants n’est exposée au WebView2.
- Limitation, sérialisation et absence de retry automatique des appels Xtream catalogue et métadonnées afin de réduire les risques de blocage ou de bannissement fournisseur.

## [1.0.0] - 2026-09-28

### Added

- Prise en charge de plusieurs fournisseurs Xtream Codes et Stalker/MAG.
- Assistant d’ajout de fournisseur avec détection, test de connexion et sélection des catégories.
- Navigation Live, Films et Séries, avec fiches détaillées et épisodes.
- Recherche locale séparée par fournisseur et construction automatique du premier index.
- Lecteur natif libmpv intégré, sans processus `mpv.exe` externe.
- Lecture Live, Films et épisodes, avec changement d’épisode sans fermer le lecteur.
- Sélection des pistes audio MULTi et des sous-titres, volume, seek, pause, reprise et plein écran.
- Téléchargement des Films et épisodes avec progression, débit et annulation.
- Copie contrôlée d’un lien média par le bridge C#.
- Interface Windows, icônes, package portable x64 et installateur avec mise à jour sur place.

### Security

- Secrets fournisseur protégés par Windows DPAPI CurrentUser.
- Aucun fournisseur ni secret préconfiguré dans les sources ou packages.
- Tokens Stalker conservés uniquement en mémoire.
- Aucune URL média contenant des identifiants transmise au JavaScript.
- Masquage centralisé des données sensibles dans les journaux et messages d’erreur.
- Runtime libmpv téléchargé séparément et vérifié par empreinte SHA-256 pendant le packaging automatisé.
