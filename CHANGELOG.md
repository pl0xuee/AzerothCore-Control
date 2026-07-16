# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **Startup-failure diagnostics now surface the real error line** instead of the trailing cleanup output —
  e.g. a "database version mismatch" line rather than authserver's "All connections … closed" message.
  More output is retained so the cause is captured even when it precedes the shutdown sequence.

## [0.1.3] - 2026-07-16

### Added
- **Module changelog** — selecting a module now shows the commits that will be pulled ("what changed"):
  short SHA, message, author, and date for each incoming commit.
- **Startup-failure diagnostics** — if a server exits right after starting (e.g. the database is
  unreachable), the notification now includes the server's last output line explaining why, instead of
  silently doing nothing.
- **Fast startup-failure breaker** — a server that dies immediately several times in a row (can't start)
  now stops being retried quickly, instead of crash-looping and churning restarts/notifications.
- **MySQL auto-detection** — "Auto-detect paths" now also finds your MySQL/MariaDB Windows service and
  reads your database names and connection details straight from your `worldserver.conf` (real `.conf`
  only — never the `.conf.dist` templates).

### Changed
- The main window opens as a normal centered window (no longer maximized on launch).
- Launching a second copy of the app now **brings the running window to the foreground** instead of doing
  nothing.
- The app no longer exits when its window is closed — it stays in the tray until you choose Quit.
- Starting a server now verifies the run directory and executable exist first, with a clear error if not.
- Any unhandled error is written in full to `%AppData%\AzerothCoreControl\last-crash.txt` and shown in a
  dialog, so problems can be diagnosed instead of vanishing.

### Fixed
- **Right-clicking the system-tray icon no longer crashes** the app — the tray menu is fully isolated from
  the app theme (themed menu styles were crashing the tray popup), uses commands instead of code-behind
  handlers, and a global exception handler now logs any UI error instead of terminating.
- **Bringing the app to the foreground no longer crashes** — fixed the actual cause: `<Run>` text bindings
  default to two-way in WPF, which threw on the read-only version/repository fields; those bindings are now
  one-way. The show/restore path was also hardened as a safety net.
- **"Restart world" no longer occasionally leaves the server stopped** — stopping now waits for the state
  to fully settle before a restart begins (fixed a race between the drain and the exit handler).
- **The auth server no longer restarts endlessly** — exit code 1 was being honored as an unlimited,
  no-backoff "restart request", but for authserver (which has no restart command) exit 1 just means an
  error. It's now treated as a crash, and a server that "requests a restart" but dies immediately is
  handled as a startup failure so the breaker can stop the loop.
- A failed launch or failed auto-restart no longer leaves a server stuck in the "Starting…" state.
- **The app no longer freezes when servers start** — server console output is batched to the UI instead of
  marshaling every line individually (startup emits thousands of lines); auto-start on launch runs entirely
  off the UI thread; toast notifications are shown off-thread (the first Windows toast can stall on
  activation); and the console auto-scroll coalesces a burst of lines into a single scroll.
- Closed a race that could spawn a **duplicate server process** when a manual start collided with an
  automatic restart.
- Update checks that run on a background thread no longer touch UI state off-thread; the in-place update
  batch no longer CPU-spins while waiting for the app to exit.

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

[0.1.3]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.3
[0.1.1]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.1
[0.1.0]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.0
