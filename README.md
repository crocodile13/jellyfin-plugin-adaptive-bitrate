<p align="center"><img src="assets/logo.svg" width="120" alt="Adaptive Bitrate logo"></p>

<h1 align="center">Adaptive Bitrate for Jellyfin</h1>

<p align="center">
  <a href="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/integration.yml"><img alt="Integration" src="https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/actions/workflows/integration.yml/badge.svg"></a>
  <img alt="Jellyfin 12.2 to 12.4" src="https://img.shields.io/badge/Jellyfin-12.2%E2%80%9312.4-00a4dc">
  <a href="LICENSE"><img alt="GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
</p>

<p align="center"><b>English</b> · <a href="README.fr.md">Français</a></p>

Quality that follows the network, without a cut. Jellyfin measures the connection once, when playback starts, and then streams a single quality: when the network weakens (Wi-Fi in another room, a train, a busy evening), the picture stops to buffer, or playback restarts at a lower quality. This plugin puts a **real quality ladder** in every HLS stream Jellyfin serves, so the player steps down when the network weakens and back up when it recovers — on its own, without restarting.

## How it works

- When Jellyfin builds an HLS playlist, I add lower qualities under the one it chose, always down to 700 kb/s, in regular steps (each at least half the bitrate above, 4 at most), with a picture size that fits the bitrate.
- **Each quality is produced by Jellyfin itself**, with the same audio track, the same subtitles (burned-in ones too) and the same playback session: only the bitrate and picture size change.
- **The player switches by itself**: hls.js in the browser, ExoPlayer on Android, AVPlayer on Apple devices. It loads and buffers the new quality *before* switching, at a segment boundary: no cut, no restart, the sound and subtitles go on.
- **A quality nobody uses costs nothing**: it is only encoded when a player switches to it, and Jellyfin stops the previous encoding as soon as the new one starts.
- Optional: send remote players through the adaptive stream instead of playing the file directly (the video is copied as is while the network keeps up, and only transcoded when the player steps down).
- Optional: **server control**. The server times the sending of every segment and can wait before letting the quality go back up after a drop (30 s by default): on a network going up and down, a quarter fewer quality changes, without more stalls (measured in the browser; off by default until checked on TV apps).
- **Diagnostic overlay** (off by default): during playback in the browser, the quality downloaded, the bitrate actually received, the buffer, the picture displayed, dropped frames and the latest changes.

Every switch is checked on a real Jellyfin in CI, on the segments themselves (the next quality starts exactly where the previous one ended, on a key frame, with the same audio) and in Google Chrome with the network throttled during playback.

## Installation

**Jellyfin 12.2 to 12.4**.

1. Dashboard → Plugins → Repositories → add `https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-adaptive-bitrate/main/manifest.json`.
2. Catalogue → **Adaptive Bitrate** → Install, then restart Jellyfin.
3. Dashboard → **Adaptive Bitrate**: it works as is; the page shows recent playbacks and their quality changes.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Players concerned | outside the local network | the local network is almost always enough; "all" helps with weak Wi-Fi or powerline |
| Lower qualities offered | 4 | how many qualities under the starting one (0 = none) |
| Maximum gap between two qualities | 0.5 | each quality has at least this share of the bitrate above; the ladder always reaches the minimum |
| Minimum video bitrate | 700 kb/s | no quality below |
| Higher qualities offered | 0 | above the starting quality, when the network improves (never beyond the file; exceeds the player's own limit) |
| Adaptive stream instead of direct play | off | remote players get the adaptive stream instead of the file (experimental) |
| Server control | off | wait before going back up (30 s); minimum gap between changes, bitrate margin and measured segments are automatic by default (from the segment length and how steady the connection is); optional step-down ahead, timed from the encoder start measured on the server |
| Diagnostic overlay | off | administrators only, or everyone |
| Excluded apps | none | apps that keep Jellyfin's stream as is |

More in the [wiki](https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/wiki).

## Limits

- Live TV is left as is (opening a live stream is slow; Jellyfin does the same).
- A file played directly (not through HLS) has a single quality, unless the "adaptive stream instead of direct play" option is on.
- A player that ignores the qualities of an HLS playlist keeps the first one, as before.

## Contributing

Bug reports and ideas are welcome ([issues](https://github.com/crocodile13/jellyfin-plugin-adaptive-bitrate/issues)); see [CONTRIBUTING.md](CONTRIBUTING.md). I review and merge pull requests.

## AI

I build this plugin with Claude Code (Anthropic). I decide what goes in, I review every change, and CI plus the integration test on a real Jellyfin check each one.

## License

GPL-3.0.
