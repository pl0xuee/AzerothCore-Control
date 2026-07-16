# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - 2026-07-15

### Added
- **Dark gunmetal theme** applied across every control, with a header bar.
- Application **icon** (shield + uptime pulse) on the window, system tray, and executable.
- **Per-server CPU % and memory** usage on the dashboard cards, sampled once per second.
- Individual **Start** / **Stop** buttons on each dashboard server card, alongside Start all / Stop all.
- Dedicated **Updates** tab for updating the app itself: installed vs. latest version and release notes.
- **Automatic in-place updates** — the app can check on launch/periodically and download & swap the new
  build over the running executable, then relaunch (a real install, not just a redownload). Toggle
  "automatically check" and "automatically download & install" in the Updates tab.
- **Browse** buttons on every path field in Settings (folder and file pickers).
- Console **auto-scrolls** to the newest output (unless you scroll up to read history).
- Change-log-driven release notes: each GitHub Release shows the matching `CHANGELOG.md` section.

### Changed
- Stopping the **world server** always performs a **safe shutdown** — it issues `.server shutdown <delay>`
  so players are warned and characters are saved before the process exits.
- Per-server Start/Stop buttons enable/disable based on the current server state.

## [0.1.0] - 2026-07-15

### Added
- Initial release.
- **Process supervisor / watchdog** for `authserver` and `worldserver` honoring AzerothCore's exit-code
  protocol (0 = clean shutdown, 1 = restart, other = crash) with exponential backoff and a crash-loop breaker.
- **Console** (stdin command passthrough) and **live logs** for both servers.
- **Module update checker/updater** (LibGit2Sharp + Octokit) with one-click Pull and Pull + Build.
- **Config-preserving build & deploy** — recompiles via CMake and deploys new binaries + `.conf.dist`
  templates without ever overwriting your `.conf` files; old binaries backed up for rollback.
- **MySQL** service monitoring, **mysqldump** backups with retention, and **scheduled restarts** with
  in-game warnings.
- **Notifications** via Windows toast, Discord webhook, and email.
- **System tray**, single-instance guard, and launch-on-boot.
- **Self-update** from this repository's GitHub Releases.
- 33 unit tests covering the watchdog exit-code policy and config preservation.

[0.1.1]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.1
[0.1.0]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.0
