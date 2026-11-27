# Adaptive Bitrate — notes de travail

Plugin Jellyfin 12.2 à 12.4 (.NET 10 ; compilé contre 12.2, la CI compile aussi contre les derniers paquets 12.x) : une vraie
échelle de qualités dans les playlists HLS de Jellyfin, pour que le lecteur change de qualité avec le réseau, sans coupure.
Mainteneur : Marien (crocodile13). Projet frère de Reco+ (`../recoplus`), mêmes règles. Dépôt public : jamais de donnée
réelle, d'adresse, de nom de machine ni de secret.

## Principe (vérifié dans les sources de Jellyfin et de jellyfin-web)

- Jellyfin mesure le débit une fois au début de la lecture (jellyfin-web : `bitrateTest`, `maxStreamingBitrate`) puis sert
  une seule qualité. Son « adaptive bitrate » d'origine (`DynamicHlsHelper`, `EnableAdaptiveBitrateStreaming`) n'ajoute
  que deux variantes à peine plus basses (−50 kb/s à −2 Mb/s), hors réseau local seulement, jamais en copie vidéo.
- jellyfin-web lit le HLS avec hls.js, niveaux automatiques activés (aucune option `autoLevel` coupée) ; ExoPlayer et AVPlayer
  aussi : une playlist maîtresse à plusieurs qualités suffit, le lecteur charge la nouvelle qualité avant de basculer.
- `HlsFilter` (filtre MVC, `DynamicHls.GetMasterHlsVideoPlaylist`) : laisse Jellyfin produire sa playlist
  (`enableAdaptiveBitrateStreaming` = false), puis rappelle la même action pour chaque marche avec `videoBitRate`,
  `maxHeight`, `allowVideoStreamCopy`/`enableAutoStreamCopy` = false, tout le reste identique (piste audio, sous-titres,
  conteneur, `PlaySessionId`). **Piège** : Jellyfin construit l'adresse de la qualité à partir de la requête HTTP
  (`HttpContext.Request.QueryString`), pas des paramètres de l'action : la requête est remplacée le temps de l'appel puis
  rétablie. Les qualités ajoutées viennent après celles de Jellyfin (un lecteur qui part de la première commence à la
  qualité choisie par Jellyfin). Forme de l'action inattendue (`Required`) : rien n'est modifié, la page le dit.
- Même `PlaySessionId` pour toutes les marches : quand une marche démarre son encodage, Jellyfin arrête celui des autres
  (`KillTranscodingJobs(deviceId, playSessionId)` dans `DynamicHlsController`) : un seul ffmpeg à la fois.
- `GetVariantHlsVideoPlaylist` (main.m3u8) : noté dans `SwitchLog` (borné, en mémoire) pour la page.
- `PlaybackInfoFilter` (option `PreferAdaptive`, coupée) : `enableDirectPlay` = false dans la requête (le paramètre de la
  requête l'emporte sur le corps, `MediaInfoController`).

## Organisation

- `src/AdaptiveBitrate.Core/` : pur, testable seul. `Ladder` (marches : débit × Step jusqu'au minimum, hauteur continue
  1080 × (débit/5,8 Mb/s)^0,52 ramenée à 30 images/s, jamais plus que la source), `MasterPlaylist` (lecture, fusion),
  `Policy` (quand l'échelle s'applique), `SwitchLog`, `Texts` (+ `english.json`, `german.json`, `spanish.json`).
- `src/Jellyfin.Plugin.AdaptiveBitrate/` : `HlsFilter`, `PlaybackInfoFilter`, `Api/AdaptiveBitrateController`
  (`AdaptiveBitrate/Status`, `AdaptiveBitrate/I18n`, administration), `Web/configPage.html`.
- `tests/AdaptiveBitrate.Tests/` (xUnit, sans `[Theory]`), `tests/client/configpage.test.mjs` (jsdom),
  `tests/integration/abr.py` (vrai Jellyfin : API, segments au changement de qualité, navigateur Chrome avec réseau bridé).

## Commandes

```
./build.sh
dotnet test tests/AdaptiveBitrate.Tests -c Release
cd tests/client && npm install --no-save jsdom@24.1.3 && node configpage.test.mjs
python tools/compat.py check
```

## Livraison

Comme Reco+ : branche depuis `origin/main` à jour, PR, CI et intégration verts, rapport relu, merge `--rebase`. Versions
`1.MINEUR.CORRECTIF.0`, groupées ; monter une version = csproj (`Version`, `AssemblyVersion`, `FileVersion`), `meta.json`,
entrée en tête de `manifest.json` (changelog anglais commençant par la version puis la fenêtre Jellyfin, `targetAbi` =
compat.json from, URL/checksum/date vides), rubrique en tête de `CHANGELOG.md`. Avant la première version, le catalogue
est vide et `release.yml` ne publie rien. Commits, PR : anglais, auteur `crocodile13 <134718087+crocodile13@users.noreply.github.com>`.
Le wiki (`jellyfin-plugin-adaptive-bitrate.wiki.git`, pages anglaises et « …-fr ») suit chaque changement de comportement.

## Conventions

- Commentaires et docs XML en français ; journaux du serveur, catalogue, changelog en anglais. Interface : français (source),
  anglais, allemand, espagnol ; test `EveryDisplayedTextIsTranslated…` (mêmes paramètres, aucune traduction orpheline).
- Interface native de Jellyfin (`emby-*`, classes du thème) ; CSS de mise en page seulement. Réglage sans effet : grisé avec
  la raison. Aide : effet, défaut. Texte venu du serveur : `textContent`, jamais du HTML.
- Toute nouveauté qui change ce que voit le lecteur est mesurée par le test d'intégration (coupures avec / sans) avant de
  devenir active par défaut.
- Versions de Jellyfin : fenêtre dans `compat.json`, `tools/compat.py` (check / set / latest), `jellyfin-watch.yml`.
