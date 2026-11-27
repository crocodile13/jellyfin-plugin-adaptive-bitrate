<p align="center"><img src="assets/logo.svg" width="120" alt="Logo Adaptive Bitrate"></p>

<h1 align="center">Adaptive Bitrate pour Jellyfin</h1>

<p align="center">
  <a href="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/integration.yml"><img alt="Intégration" src="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/integration.yml/badge.svg"></a>
  <img alt="Jellyfin 12.2 à 12.4" src="https://img.shields.io/badge/Jellyfin-12.2%E2%80%9312.4-00a4dc">
  <a href="LICENSE"><img alt="GPL-3.0" src="https://img.shields.io/badge/licence-GPL--3.0-blue"></a>
</p>

<p align="center"><a href="README.md">English</a> · <b>Français</b></p>

Une qualité qui suit le réseau, sans coupure. Jellyfin mesure la connexion une fois, au début de la lecture, puis envoie une seule qualité : quand le réseau faiblit (Wi-Fi dans une autre pièce, train, soirée chargée), l'image s'arrête pour charger, ou la lecture redémarre dans une qualité plus basse. Ce plugin met une **vraie échelle de qualités** dans chaque flux HLS de Jellyfin : le lecteur descend quand le réseau faiblit et remonte quand il revient, tout seul, sans redémarrer.

## Comment ça marche

- Quand Jellyfin construit une playlist HLS, j'ajoute des qualités plus basses sous celle qu'il a choisie (par défaut 4, chacune à 60 % du débit de celle du dessus, jusqu'à 700 kb/s, avec une taille d'image adaptée au débit).
- **Chaque qualité est produite par Jellyfin lui-même**, avec la même piste audio, les mêmes sous-titres (incrustés compris) et la même session de lecture : seuls le débit et la taille de l'image changent.
- **Le lecteur change de qualité tout seul** : hls.js dans le navigateur, ExoPlayer sur Android, AVPlayer sur les appareils Apple. Il charge et met en mémoire la nouvelle qualité *avant* de basculer, à la limite d'un segment : ni coupure ni redémarrage, le son et les sous-titres continuent.
- **Une qualité qui ne sert pas ne coûte rien** : elle n'est encodée que si un lecteur y passe, et Jellyfin arrête l'encodage précédent dès que le nouveau démarre.
- En option : faire passer les lecteurs distants par le flux adaptatif plutôt que par la lecture directe du fichier (la vidéo est copiée telle quelle tant que le réseau suit, et transcodée seulement quand le lecteur descend).

Chaque changement de qualité est vérifié sur un vrai Jellyfin dans la CI, sur les segments eux-mêmes (la qualité suivante commence exactement où la précédente s'arrête, sur une image clé, avec le même audio) et dans Google Chrome avec le réseau bridé pendant la lecture.

## Installation

**Jellyfin 12.2 à 12.4**.

1. Tableau de bord → Extensions → Dépôts → ajouter `https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-adaptive-bitrate/main/manifest.json`.
2. Catalogue → **Adaptive Bitrate** → Installer, puis redémarrer Jellyfin.
3. Tableau de bord → **Adaptive Bitrate** : il marche tel quel ; la page montre les lectures récentes et leurs changements de qualité.

## Réglages

| Réglage | Par défaut | Effet |
|---|---|---|
| Lecteurs concernés | hors du réseau local | le réseau local suffit presque toujours ; « tous » aide avec un Wi-Fi faible ou du CPL |
| Qualités plus basses proposées | 4 | combien de qualités sous celle de départ (0 = aucune) |
| Écart entre deux qualités | 0,6 | chaque qualité a ce rapport du débit de celle du dessus |
| Débit vidéo minimal | 700 kb/s | aucune qualité en dessous |
| Qualités plus hautes proposées | 0 | au-dessus de la qualité de départ, quand le réseau s'améliore (jamais au-delà du fichier ; dépasse la limite réglée dans le lecteur) |
| Flux adaptatif plutôt que lecture directe | coupé | les lecteurs distants reçoivent le flux adaptatif au lieu du fichier (expérimental) |
| Applications exclues | aucune | applications qui gardent le flux de Jellyfin tel quel |

Plus de détails dans le [wiki](https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/wiki/Accueil-fr).

## Limites

- La télévision en direct reste telle quelle (ouvrir un flux en direct est lent ; Jellyfin fait de même).
- Un fichier lu directement (pas en HLS) n'a qu'une qualité, sauf avec l'option « flux adaptatif plutôt que lecture directe ».
- Un lecteur qui ignore les qualités d'une playlist HLS garde la première, comme avant.

## Contribuer

Signalements et idées bienvenus ([issues](https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/issues)) ; voir [CONTRIBUTING.md](CONTRIBUTING.md). Je relis et merge les pull requests.

## IA

Je construis ce plugin avec Claude Code (Anthropic). Je décide de ce qui entre, je relis chaque modification, et la CI ainsi que le test d'intégration sur un vrai Jellyfin vérifient chacune.

## Licence

GPL-3.0.
