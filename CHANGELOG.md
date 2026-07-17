# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.9] - 2026-07-16

### Added
- **Review CMake settings before building** — a new Build setting (on by default) opens cmake-gui pointed at
  your source and build directories and waits for you to close it before the compile starts, so options can
  be checked and re-generated first. Only user-initiated builds can reach this; scheduled jobs never build,
  so nothing can sit blocked on a dialog nobody is watching. cmake-gui is found next to your cmake by
  default, with an optional explicit path.
- **Build settings are editable** — the cmake and cmake-gui paths now appear in Settings; previously the
  build config existed only in settings.json with no way to reach it from the app.

### Changed
- **Builds always use RelWithDebInfo** — the configuration was a setting defaulting to `Release`, which
  optimises away the debug symbols a crash dump needs. It is now fixed at RelWithDebInfo and the setting is
  gone; any `Configuration` value in your settings.json is ignored. The initial configure also passes
  `-DCMAKE_BUILD_TYPE=RelWithDebInfo` so single-config generators (Ninja, Makefiles) land there too, not
  just Visual Studio.
- **Modules list is easier to read** — a selected row was painted with both the row highlight and a green
  cell tint stacked on top of each other, which made the row glare. Selection is now drawn once, in a muted
  green-tinted surface. Commit SHAs are monospaced, module status is coloured (amber when behind, red on
  error), and the action result moved into its own "Last result" column so a long build message no longer
  pushes the buttons around. A failed build now reads red instead of muted grey.
- **Pull + Build is disabled while an update is running**, rather than being re-clickable mid-build.

## [0.1.8] - 2026-07-16

### Changed
- **Separate consoles for world and auth** — both servers shared one output list, so world's startup flood
  buried auth's lines and the `[auth] ` prefix was the only way to tell them apart. The Console tab now has
  a World and an Auth pane, each with its own scrollback, line count, and Clear button.
- **Console output is easier to read** — each line now carries an arrival timestamp in a muted gutter and is
  coloured by severity (amber warnings, red errors, green for commands you typed). Long lines wrap instead
  of running off a horizontal scrollbar, and rows can be multi-selected and copied.
- **The command input is world-only** — authserver ignores stdin, so sending it commands was always a no-op.

## [0.1.7] - 2026-07-16

### Fixed
- **Module config files are now read** — detection only opened `worldserver.conf` and `authserver.conf`, but
  modules ship their own config into a `modules` subfolder (mod-playerbots puts `PlayerbotsDatabaseInfo` in
  `etc/modules/playerbots.conf`), so a module's database was still missed even though any `*DatabaseInfo`
  key is matched. Every `.conf` in a `modules` subfolder of each searched config directory is now read;
  `.conf.dist` templates remain ignored.

## [0.1.6] - 2026-07-16

### Fixed
- **Module databases are now detected** — detection matched a fixed list of the three core keys
  (`LoginDatabaseInfo`, `CharacterDatabaseInfo`, `WorldDatabaseInfo`), so a module's database was ignored
  and left out of backups. Playerbots' `PlayerbotsDatabaseInfo` was the case that surfaced this. Any config
  key ending in `DatabaseInfo` is now read, so module databases are picked up without this app needing to
  know the module exists.

## [0.1.5] - 2026-07-16

### Added
- **App version in the status bar** — the running version is shown in the bottom-left corner, read from the
  assembly rather than hardcoded, so it always matches the build.

### Fixed
- **Database names are now detected from the real config.** Detection only looked inside the run directory
  (where `worldserver.exe` lives), missing AzerothCore's Windows layout that keeps configs in a sibling
  folder (`env/dist/bin/` vs `env/dist/etc/`). Nothing was found, so the settings screen silently fell back
  to the built-in `acore_auth`/`acore_characters`/`acore_world` defaults while claiming they came from your
  `worldserver.conf`. The run directory's `etc/` and `configs/` subfolders — and the same three relative to
  its parent — are now searched as well, and auto-detect reports when no config was found instead of
  staying quiet.

## [0.1.4] - 2026-07-16

### Added
- **Automatic orphaned-process cleanup** — before starting a server, any leftover instance of that exact
  executable is terminated first. This prevents the "Could not bind to 0.0.0.0:3724 — only one usage of
  each socket address is permitted" failure caused by a stale authserver/worldserver still holding its port.

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

[0.1.4]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.4
[0.1.3]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.3
[0.1.1]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.1
[0.1.0]: https://github.com/pl0xuee/AzerothCore-Control/releases/tag/v0.1.0
