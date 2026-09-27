# Security Policy

## Versions prises en charge

La branche stable et la dernière release `1.x` reçoivent les correctifs de sécurité.

## Signaler une vulnérabilité

N'ouvrez **jamais** une issue publique contenant :

- URL privée de fournisseur IPTV ;
- username ou password ;
- adresse MAC Stalker/MAG ;
- token, header Authorization ou cookie ;
- URL média credentialisée ;
- base SQLite, fichier DPAPI ou journal contenant des données personnelles.

Utilisez de préférence **GitHub Private Vulnerability Reporting** lorsqu'il est activé sur le dépôt. À défaut, contactez le mainteneur via son profil GitHub sans publier de secret dans une discussion publique.

Merci d'indiquer :

- version d'IPTV Explorer ;
- système Windows concerné ;
- description et impact ;
- étapes de reproduction minimales avec valeurs fictives ;
- proposition de correctif si disponible.

## Modèle de sécurité

IPTV Explorer stocke sa configuration sous `%LOCALAPPDATA%\IPTV Explorer`. Les secrets fournisseur sont chiffrés avec Windows DPAPI `CurrentUser` et ne sont pas stockés en clair dans SQLite. Les tokens Stalker sont conservés en mémoire. Les URL média sensibles sont résolues côté C# et ne sont pas renvoyées à l'interface JavaScript.

Aucun fournisseur, credential ou contenu IPTV n'est livré avec le logiciel.
