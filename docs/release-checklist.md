# Checklist Release 2.0

Cette checklist doit être entièrement validée avant de créer le tag `v2.0.0`.

## 1. Source et sécurité

- [ ] `git status --short` vide sur la branche de release.
- [ ] `dotnet build -c Release` réussi sans avertissement.
- [ ] `dotnet test -c Release` réussi.
- [ ] Aucun fichier `.sqlite`, `.db`, `.env`, `.key`, `.pfx`, log, cache ou secret suivi par Git.
- [ ] Aucun username, password, MAC, token, Authorization, cookie ou URL fournisseur réel dans l'arbre courant.
- [ ] Audit de l'historique Git avec un scanner de secrets avant passage Public.
- [ ] Fresh install : `%LOCALAPPDATA%\IPTV Explorer` absent au premier lancement et aucun fournisseur préconfiguré.

## 2. Validation fonctionnelle

### Xtream

- [ ] Onboarding et authentification.
- [ ] Catégories Live / Films / Séries.
- [ ] Lecture Live.
- [ ] Lecture VOD.
- [ ] Lecture d'un épisode de série.
- [ ] Recherche après construction de l'index.

### Stalker/MAG

- [ ] Onboarding et authentification.
- [ ] Catégories Live / Films / Séries.
- [ ] Lecture Live.
- [ ] Lecture VOD.
- [ ] Lecture d'un épisode de série.
- [ ] Recherche après construction de l'index.

### Lecteur

- [ ] Play / Pause / Stop.
- [ ] Seek avant / arrière.
- [ ] Volume.
- [ ] Audio MULTi.
- [ ] Sous-titres.
- [ ] Changement de piste sans blocage permanent.
- [ ] Maximisation et plein écran.
- [ ] Icône fenêtre et barre des tâches.

### Actions média

- [ ] `Télécharger` : téléchargement VOD complet vers un chemin choisi.
- [ ] `Copier le lien` : copie volontaire vers le presse-papiers Windows.
- [ ] Aucun secret n'est renvoyé au JavaScript pendant ces actions.

## 3. Persistance

- [ ] Fermer et rouvrir l'application : fournisseurs retrouvés.
- [ ] DPAPI fonctionne pour le même compte Windows.
- [ ] Un provider désactivé reste désactivé.
- [ ] Les indexes restent séparés par fournisseur.

## 4. Packaging

Préparer le runtime libmpv épinglé :

```powershell
.\scripts\Get-LibMpvRuntime.ps1
```

Avec Inno Setup 6 installé :

```powershell
.\scripts\Publish-Release.ps1 -Version 2.0.0 -BuildInstaller
```

Attendu :

```text
dist\IPTV-Explorer-Setup-2.0.0-x64.exe
dist\IPTV-Explorer-2.0.0-win-x64.zip
dist\SHA256SUMS.txt
```

- [ ] Tester l'installateur sur une machine / session propre.
- [ ] Tester le ZIP portable depuis un dossier hors du dépôt Git.
- [ ] Vérifier la présence de `native\mpv\libmpv-2.dll` dans les deux distributions.
- [ ] Vérifier les hashes SHA-256.

## 5. GitHub

- [ ] README, LICENSE, CHANGELOG, SECURITY, CONTRIBUTING et THIRD_PARTY_NOTICES présents.
- [ ] Workflow CI vert.
- [ ] Workflow `Windows Release` testé manuellement via `workflow_dispatch`.
- [ ] Merge final sur `main`.
- [ ] Tag `v2.0.0` créé depuis le commit validé.
- [ ] GitHub Release contient l'EXE, le ZIP et `SHA256SUMS.txt`.
- [ ] Télécharger les artefacts de la Release et les tester une dernière fois.
- [ ] Seulement ensuite : passer le dépôt de Private à Public.
