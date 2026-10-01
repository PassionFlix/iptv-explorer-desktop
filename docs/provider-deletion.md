# Suppression d’un fournisseur

La suppression ne contacte jamais le fournisseur. Elle évince d’abord les ressources runtime, puis, lorsque l’utilisateur demande aussi la suppression de l’index local, prend le verrou d’écriture de cet index, vide son pool SQLite ciblé et supprime `search-{providerKey}.sqlite` avec ses fichiers `-wal`, `-shm` et `-journal`. Trois délais courts de 25, 50 et 75 ms absorbent uniquement la libération tardive d’un handle Windows.

Le fichier principal reste en place jusqu’à la suppression réussie des fichiers annexes. Un échec critique de ce nettoyage interrompt le workflow avant toute suppression du fournisseur ou de son secret.

Ensuite, une transaction SQLite unique supprime le fournisseur et remet `app.preferences.activeProviderKey` à `null` lorsque nécessaire. Les clés étrangères existantes suppriment avec lui les catégories et politiques, la santé, les jobs de reconstruction, l’historique de lecture, les préférences de lecture, `series_artwork`, `catalog_snapshots`, `catalog_items` et `media_details`. Le secret DPAPI est supprimé seulement après le commit de cette transaction.

`RemoveLocalData=false` conserve uniquement la famille de l’index de recherche détachée, conformément au libellé actuel « Supprimer également l’index local ? ». Les données relationnelles liées à l’identité supprimée continuent de suivre les cascades historiques de la base. `RemoveLocalData=true` supprime aussi l’index et garantit qu’un fournisseur recréé avec la même clé ne peut pas relire cet ancien index.

Les clients Stalker mémorisés sont évincés avant et après la mutation durable. Les clients temporaires d’onboarding utilisent une clé de probe propre au brouillon et sont évincés avant la suppression de leur secret temporaire.
