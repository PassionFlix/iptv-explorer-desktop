# Checklist Windows réelle — Phase 2

Inscrire date, version Windows, version SDK/WebView2, opérateur et résultat pour chaque ligne. Ne jamais copier de credential ou d’URL privée dans ce document.

- [ ] `dotnet --info`, restore, build Release et suite de tests complète : 0 erreur.
- [ ] La fenêtre WPF et les ressources WebView2 locales s’ouvrent sans navigation externe.
- [ ] Fresh install : zéro provider et création des seuls dossiers runtime attendus.
- [ ] Onboarding Xtream fictif/réel saisi manuellement : test, catégories, All/None/Custom, activation explicite, redémarrage.
- [ ] Onboarding Stalker/MAG saisi manuellement : handshake, profile, catégories, pagination, détails, redémarrage.
- [ ] SQLite et logs ne contiennent ni password, MAC, token, Authorization ni URL média credentialisée.
- [ ] Les blobs secrets sont chiffrés DPAPI CurrentUser et ne sont pas transmis à JavaScript.
- [ ] Deux providers : bascule immédiate et persistance du choix actif, sans contamination des catégories/index.
- [ ] Live, Films et Séries : navigation, pagination, détails, images lazy et placeholders.
- [ ] Stalker Live : ouvrir `AFRICA`, noter 2–3 chaînes, puis ouvrir `BEIN SPORTS`; les listes doivent être différentes et ne pas conserver les mêmes chaînes françaises.
- [ ] Stalker VOD : ouvrir deux films différents; titre, poster, résumé, réalisation, distribution et métadonnées doivent appartenir à chaque film.
- [ ] Stalker Séries : ouvrir `OPJ`, vérifier saisons distinctes et épisodes cohérents, puis ouvrir `MobLand`; la fiche doit afficher des données différentes.
- [ ] Stalker épisode : cliquer `Lire` sur deux épisodes différents; le placeholder Player doit recevoir l’identité du bon épisode, sans URL sensible dans JavaScript.
- [ ] Modal VOD/Série : poster limité à gauche, texte à droite, saisons sous l’en-tête, scroll vertical, fermeture accessible et aucun débordement horizontal.
- [ ] Catégorie Stalker avec entité HTML : `CHILE &amp; BOLIVIA` s’affiche `CHILE & BOLIVIA` sans altérer son identifiant distant.
- [ ] Logos Live transparents, absents ou en erreur : conteneur fixe, `object-fit: contain`, fallback lisible et aucune grande zone blanche.
- [ ] Changement rapide d’écran/catégorie : requête annulée, aucune réponse ancienne injectée dans la nouvelle vue.
- [ ] Index : file, progression, recherche paginée et dirty state après changement de catégories.
- [ ] Arrêt pendant index : job `interrupted`, ancien index toujours interrogeable, reconstruction possible.
- [ ] Provider hors ligne : démarrage possible, dashboard indisponible, recherche locale toujours utilisable.
- [ ] Édition avec secret vide : secret conservé; nouvelle valeur : testée puis remplacée.
- [ ] Désactivation et suppression : confirmations, retrait du sélecteur, secret supprimé, choix index respecté.
- [ ] Boutons Lire : écran placeholder, aucune ouverture navigateur, aucune URL média visible dans la WebView.
- [ ] Mesurer démarrage, accueil, 500/200/100 catégories, grilles et recherche; consigner les temps et éventuels ralentissements.
- [ ] Refaire un fresh install final après déplacement des données de test.

Les tests avec un provider utilisateur restent manuels et locaux à `%LOCALAPPDATA%`; ils ne doivent jamais devenir des fixtures ou entrer dans le dépôt/source.
