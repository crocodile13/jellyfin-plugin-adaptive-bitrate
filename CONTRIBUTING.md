# Contributing

Thanks for helping! Bug reports, ideas and pull requests are welcome. Français accepté.

- **Bugs**: open an issue with the bug form: your Jellyfin version, the app you play with, and what the plugin page shows
  under "Status" (recent playbacks and their quality changes). Never paste API keys, tokens or server addresses.
- **Pull requests**: small and focused. `./build.sh` must pass (unit tests, build, package); new behaviour needs a test
  (core: xUnit in `tests/AdaptiveBitrate.Tests`; settings page: jsdom in `tests/client`). Anything that changes what a player
  receives is checked by the integration test on a real Jellyfin (`tests/integration/abr.py`), which runs on every pull request.
- **Texts shown to users**: French is the source; add the English, German and Spanish translations in
  `src/AdaptiveBitrate.Core/*.json` (a test checks every text).
- I review and merge pull requests. CI and the integration test must be green.
