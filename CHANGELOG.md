# Changelog

Adaptive Bitrate versions, newest first. Each line is the changelog shown by Jellyfin's plugin catalogue.
Compatibility: see the Jellyfin range of each version below (`targetAbi` in `manifest.json`).

## 1.0.0 — First version

- **1.0.0** (Jellyfin 12.2–12.4) — first version. Every HLS stream Jellyfin serves gets a real quality ladder: by default 4 lower qualities down to 700 kb/s, each produced by Jellyfin itself with the same audio track, subtitles (burned-in ones too) and playback session. The player (web, Android, Apple devices) steps down when the network weakens and back up when it recovers, loading the new quality before switching: no cut, no restart. A lower quality costs nothing until a player uses it. Measured on a real Jellyfin in Chrome with the network throttled to 1.6 Mb/s for a minute: 7 s of stalls instead of 46 s, on Jellyfin 12.2 and 12.4. Optional: remote players can get the adaptive stream instead of direct play (experimental). Settings page in English, French, German and Spanish.
