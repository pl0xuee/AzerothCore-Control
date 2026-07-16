<p align="center">
  <img src="docs/icon.png" width="120" alt="AzerothCore Control icon" />
</p>

# AzerothCore Control

[![CI](https://github.com/pl0xuee/AzerothCore-Control/actions/workflows/ci.yml/badge.svg)](https://github.com/pl0xuee/AzerothCore-Control/actions/workflows/ci.yml)

A Windows desktop app that supervises an [AzerothCore](https://www.azerothcore.org/) server:
keeps `authserver` and `worldserver` running, checks GitHub for module updates, and can rebuild &
deploy new binaries **without touching your custom `.conf` files**. Dark gunmetal UI, runs in the tray.

## Features

- **Live resource usage** — per-server CPU % and memory shown on the dashboard cards.
- **Process supervisor / watchdog** — start/stop/restart both servers from one place, with auto-restart
  that respects AzerothCore's [exit-code protocol](https://www.azerothcore.org/wiki/exitcodes):
  - exit `0` (clean shutdown) → stays down
  - exit `1` (`.server restart`) → restarts immediately
  - any other exit / crash → restarts with exponential backoff, and a **crash-loop breaker** that halts
    and alerts you after too many crashes in a short window.
- **Console** — send worldserver commands (`.server shutdown`, `.account create`, …) over stdin, with live
  auto-scrolling output.
- **Live logs** — streamed stdout/stderr from both servers.
- **Module updates** — scans the source `modules/` folder, compares each module against its GitHub remote,
  and offers one-click **Pull** or **Pull + Build**.
- **Automated rebuild + deploy** — runs CMake, then copies fresh `authserver.exe` / `worldserver.exe` and
  updated `*.conf.dist` templates into the run directory. **Your `*.conf` files are never overwritten**;
  old binaries are backed up as `*.bak` for rollback. Newly added config keys are surfaced (never auto-applied).
- **Scheduled restarts & backups** — nightly graceful restarts with in-game warnings, plus `mysqldump` backups
  with retention.
- **MySQL monitoring** — checks/starts the MySQL Windows service before launching the servers.
- **Notifications** — Windows toast, Discord webhook, and email (SMTP) on crashes, breaker trips, and updates.
- **System tray + launch-on-boot** — minimizes to the tray and keeps supervising; optional autostart.
- **Self-update** — a dedicated **Updates** tab reads this repo's GitHub Releases, shows installed vs. latest
  version and release notes, and prompts when a newer version is available.

## Requirements (on the server machine)

- Windows 10/11.
- A built AzerothCore install (the run directory with `worldserver.exe`, `authserver.exe`, and `.conf` files).
- For the rebuild feature: the AzerothCore source checkout, a configured CMake build directory, and Visual Studio
  build tools (already present on your box).
- MySQL/MariaDB installed as a Windows service.

## Getting started

1. Download the latest `AzerothCoreControl-*-win-x64.zip` from the
   [Releases](https://github.com/pl0xuee/AzerothCore-Control/releases) page and extract it.
2. Run `AzerothCoreControl.exe` (it requests administrator rights — needed to control the MySQL service).
3. Open **Settings**, click **Auto-detect paths** (or set them manually), and **Save**.
4. Use the **Dashboard** to start the servers.

## Project layout

```
src/
  AzerothCoreControl.Core/   # all non-UI logic (supervisor, updater, build/deploy, backups, ...)
  AzerothCoreControl.App/    # WPF UI, system tray, notifications
tests/
  AzerothCoreControl.Core.Tests/   # unit tests (watchdog policy, config preservation, ...)
```

The `Core` library targets plain `net8.0` and is fully unit-tested; the WPF app targets
`net8.0-windows`.

## Building from source

```sh
dotnet build
dotnet test
```

To produce the packaged app (on Windows):

```sh
dotnet publish src/AzerothCoreControl.App/AzerothCoreControl.App.csproj \
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Releasing

Pushing a tag like `v0.1.0` triggers the [release workflow](.github/workflows/release.yml), which builds the
single-file win-x64 app and publishes it as a GitHub Release. The in-app updater picks it up automatically.

```sh
git tag v0.1.0
git push origin v0.1.0
```

## License

[MIT](LICENSE)
