# Changelog

Adaptive Bitrate versions, newest first. Each line is the changelog shown by Jellyfin's plugin catalogue.
Compatibility: see the Jellyfin range of each version below (`targetAbi` in `manifest.json`).

## 1.1.0 — Diagnostic overlay, ladder down to the minimum, server control

- **1.1.0** (Jellyfin 12.2–12.4) — a diagnostic overlay shows, during playback in the browser, the quality downloaded, the bitrate actually received, the buffer, the picture displayed, dropped frames and the latest quality changes (off by default; administrators or everyone). The ladder always goes down to the minimum bitrate in regular steps, the setting becoming a maximum gap between two qualities (each at least half the bitrate above by default). New optional server control: the server times the sending of every segment and can wait before letting the quality go back up after a drop (30 s by default); measured in the browser on a network going up and down, a quarter fewer quality changes without more stalls. Off by default until checked on TV apps. The catalogue only lists a version once it can be installed.

## 1.0.0 — First version

- **1.0.0** (Jellyfin 12.2–12.4) — first version. Every HLS stream Jellyfin serves gets a real quality ladder: by default 4 lower qualities down to 700 kb/s, each produced by Jellyfin itself with the same audio track, subtitles (burned-in ones too) and playback session. The player (web, Android, Apple devices) steps down when the network weakens and back up when it recovers, loading the new quality before switching: no cut, no restart. A lower quality costs nothing until a player uses it. Measured on a real Jellyfin in Chrome with the network throttled to 1.6 Mb/s for a minute: 7 s of stalls instead of 46 s, on Jellyfin 12.2 and 12.4. Optional: remote players can get the adaptive stream instead of direct play (experimental). Settings page in English, French, German and Spanish.
