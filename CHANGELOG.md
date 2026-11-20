# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-21

### Added

- Tray icon: hollow ring gauge with the 5-hour usage percentage next to it,
  color thresholds at 60% and 85%.
- Details popup on click: 5-hour, weekly and monthly limits with progress bars,
  remaining percentages and reset times.
- Large floating badge (96×52) near the tray: draggable, remembers its position,
  toggled from the context menu — solves the 16 px Windows tray cell limitation.
- Context menu: refresh, open the OpenCode console, tray icon settings,
  start with Windows, exit.
- Automatic refresh every 60 seconds and manual refresh.
- API key discovery from OpenCode's `auth.json` (`opencode-go` provider) or the
  `OPENCODE_GO_KEY` environment variable.
- `build.ps1` (in-box `csc.exe`), `install.ps1`, `uninstall.ps1`,
  `tools\make-icon.ps1`, CI build and release workflows.
- `--shot` and `--open` developer flags.
- Bilingual READMEs (English and Russian) with a language switcher.
- Prominent non-affiliation note in both READMEs, as requested by the OpenCode
  README policy for projects that use "opencode" in their name.

### Notes

- The usage endpoint is undocumented and may change.
- The UI is in Russian; localization contributions are welcome.
