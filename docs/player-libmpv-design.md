# Conception du lecteur libmpv

## Décision

Phase 3 intégrera **libmpv dans le processus WPF** par P/Invoke. Aucun `mpv.exe` ne sera lancé et aucune URL média ne figurera dans les arguments d’un processus. L’URL résolue restera un objet managé de courte durée, transmis à `mpv_command_node`/`loadfile` dans le processus.

Le projet mpv recommande libmpv comme backend d’intégration et expose une API C stable : [documentation libmpv](https://github.com/mpv-player/mpv/blob/master/DOCS/man/libmpv.rst) et [client.h](https://github.com/mpv-player/mpv/blob/master/include/mpv/client.h).

## Hôte vidéo WPF

1. Créer un contrôle `HwndHost` x64 qui possède un HWND enfant natif.
2. Créer un handle `mpv_handle` avec `mpv_create`.
3. Définir les options avant `mpv_initialize` : configuration désactivée, terminal désactivé, entrées clavier/souris désactivées, accélération matérielle selon préférence utilisateur.
4. Affecter le HWND enfant via l’option `wid`, puis initialiser libmpv. Cette voie est simple pour le premier lecteur.
5. Si la composition/airspace WPF pose problème, migrer le rendu vers `mpv_render_context` et Direct3D/OpenGL sans modifier `IPlayerService`.
6. Détruire le contexte et le handle sur le thread propriétaire lors de la fermeture.

Le callback de réveil ne fera aucun travail lourd : il signalera une boucle dédiée qui drainera `mpv_wait_event`. Les mises à jour UI seront marshalées par le `Dispatcher` WPF.

## Contrôle et événements

Les commandes `set_property`/`command_node` implémenteront lecture, pause, arrêt, seek, volume et plein écran. Les propriétés observées alimenteront :

- `pause`, `idle-active`, `core-idle` → `StateChanged` ;
- `time-pos`, `duration` → `PositionChanged` ;
- `track-list` → `TrackListChanged`.

`track-list` sera convertie vers `MediaTrack`: `id`, `type`, `lang`, `title`, `codec`, `selected`, `default`, `forced`, puis canaux/fréquence audio et dimensions/FPS/HDR vidéo lorsque disponibles. La sélection explicite utilisera `aid`, `vid`, `sid`; les sous-titres pourront être désactivés avec `sid=no`. Les préférences utilisateur seront appliquées après réception de la liste, sans imposer une langue dans la release générique.

## Distribution et licences

Le livrable x64 devra inclure une build libmpv reproductible et ses DLL dépendantes dans un sous-dossier natif, avec hash, version, notices et offre de code source correspondante. Le chargement utilisera un chemin absolu contrôlé, jamais le répertoire courant ni `PATH`.

mpv est GPLv2+ par défaut et peut être construit en mode LGPLv2.1+ avec `-Dgpl=false`, mais la documentation précise que les bibliothèques liées — notamment FFmpeg — influencent aussi la licence finale : [Copyright mpv](https://github.com/mpv-player/mpv/blob/master/Copyright). Avant distribution, figer la provenance du binaire, auditer les options de compilation et les codecs, puis faire valider les obligations de licence. Ce document n’est pas un avis juridique.

## Tests Phase 3

- lecture HLS, MPEG-TS et VOD avec URL factice issue d’un serveur fixture local de test ;
- inspection/sélection de plusieurs pistes audio, vidéo et sous-titres ;
- Forced, Full et SDH lorsque les métadonnées le permettent ;
- aucune URL sensible dans `Process Explorer`, logs, crash reports ou messages bridge ;
- redimensionnement, DPI, plein écran, veille et fermeture pendant lecture ;
- absence de chargement DLL depuis un emplacement non approuvé.
