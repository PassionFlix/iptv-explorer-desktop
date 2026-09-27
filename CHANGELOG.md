# Changelog

Toutes les modifications notables d'IPTV Explorer Desktop sont documentées ici.

Le projet suit le versionnement sémantique.

## [1.0.0] - 2026-09-27

### Ajouté

- Support multi-fournisseur Xtream Codes et Stalker/MAG.
- Onboarding fournisseur avec détection, test de connexion et sélection des catégories.
- Navigation Live, Films et Séries.
- Fiches VOD et Séries avec épisodes.
- Index de recherche local par fournisseur.
- Lecteur natif libmpv intégré sans processus `mpv.exe` externe.
- Lecture Live, VOD et épisodes.
- Sélection audio MULTi et sous-titres.
- Volume, seek, pause/reprise et plein écran.
- Téléchargement VOD et copie du lien média via le bridge C# sécurisé.
- Secrets fournisseur protégés par DPAPI CurrentUser.
- Interface Windows finalisée avec branding et icônes dédiés.
- Packaging Windows x64 portable et installateur.

### Sécurité

- Aucun fournisseur ou secret préconfiguré dans les sources ou packages.
- Tokens Stalker conservés uniquement en mémoire.
- Aucune URL média credentialisée renvoyée au JavaScript.
- Redaction centralisée des journaux et messages d'erreur.
- Runtime libmpv non stocké dans Git et récupéré avec hash SHA-256 vérifié pendant le packaging automatisé.
