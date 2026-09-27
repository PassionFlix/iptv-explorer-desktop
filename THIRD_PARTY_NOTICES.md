# Third-Party Notices

IPTV Explorer Desktop est distribué sous licence MIT, mais utilise des composants tiers soumis à leurs propres licences.

## libmpv / mpv

Les packages Release Windows x64 incluent `libmpv-2.dll`.

L'automatisation de release utilise une build communautaire publiée par :

- projet : `zhongfly/mpv-winbuild`
- variante : `mpv-dev-lgpl-x86_64`
- configuration annoncée par le fournisseur : libmpv sous LGPLv2.1+ avec composants incompatibles désactivés, FFmpeg sous LGPLv3

La source exacte, le nom de l'archive et son SHA-256 sont épinglés dans [`build/libmpv-runtime.json`](build/libmpv-runtime.json).

Sources :

- https://github.com/mpv-player/mpv
- https://github.com/zhongfly/mpv-winbuild

mpv et ses dépendances restent soumis à leurs licences respectives. Le projet IPTV Explorer ne revendique aucun droit sur ces composants.

La DLL est chargée dynamiquement. Les utilisateurs peuvent remplacer `native/mpv/libmpv-2.dll` par une build compatible de leur choix.

## Microsoft WebView2

IPTV Explorer utilise Microsoft Edge WebView2 via le package `Microsoft.Web.WebView2`. Le runtime WebView2 est distribué et licencié par Microsoft et n'est pas couvert par la licence MIT d'IPTV Explorer.

- https://developer.microsoft.com/microsoft-edge/webview2/

## Packages NuGet

Le projet utilise également des packages NuGet Microsoft et .NET, notamment WebView2, Microsoft.Extensions.* et Microsoft.Data.Sqlite. Chaque package conserve sa propre licence telle qu'indiquée dans ses métadonnées NuGet.

## Responsabilité de redistribution

Toute personne redistribuant un package modifié d'IPTV Explorer doit conserver les avis et obligations de licence applicables aux composants tiers qu'elle inclut.
