## What and why

<!-- What this changes, and the problem it solves. Link the issue: "Fixes #123". -->

## How it was checked

<!-- Tests added or run, what you looked at on a real server, the integration report (stalls with / without the ladder). -->

## Checklist

- [ ] Small and focused: one change per pull request.
- [ ] `./build.sh` passes (unit tests, build, package).
- [ ] New behaviour has a test (core: xUnit in `tests/AdaptiveBitrate.Tests`; settings page: jsdom in `tests/client`).
- [ ] **What players receive changes**: checked by the integration test (`tests/integration/abr.py`) and its report read.
- [ ] **Text shown to users**: written in French (the source language) and translated in `english.json`, `german.json` and `spanish.json`.
- [ ] **Settings**: help text and default value; nothing duplicated from an existing setting.
- [ ] Docs updated if behaviour changes: README (English and French) and the wiki page concerned.
- [ ] No secret, server address or personal data in code, logs, screenshots or this pull request.

<!-- CI and the integration test run on every pull request; I review and merge. Thank you! -->
