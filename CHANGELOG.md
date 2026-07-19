# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.21] - 2026-07-19

### Added
- **`mod-challenge-modes` now defaults to `AldebaraanMKII/mod-challenge-modes`.** The module's original repo,
  `ZhengPeiRu21/mod-challenge-modes`, still declares `OnPlayerResurrect`'s third parameter by value and stopped
  overriding the core hook when it became `bool&`. It hasn't been touched since 2025-11-25, so every install
  following it fails the entire modules target — they all compile together. Pulls correctly report "already up
  to date" while the build fails, with nothing connecting the two.
  - There's now a small built-in list of modules whose maintained home isn't the repo the catalogue finds. The
    Modules tab flags a checkout following an unmaintained upstream and offers to repoint it, with no
    configuration needed.
  - A user's own pin always wins and suppresses the built-in suggestion entirely — someone who deliberately
    pinned a module isn't then nagged towards our preference for it.
  - The wording distinguishes the two: our suggestion never claims the user pinned anything.

### Fixed
- **"Replace modules that won't pull" could destroy healthy checkouts.** `Pull` reports failure for network
  drops, TLS errors, bad credentials, rate limiting and locked repos as well as for local divergence. The
  replace path acted on all of them, so a Wi-Fi drop part-way through a twenty-module batch would move every
  remaining module out of `modules/` and then fail to clone it back — with recovery resting on a best-effort
  restore. Failures are now classified, and only a dirty tree or a diverged history can authorise a replace.
- **Re-cloning silently switched the module's branch.** `Repository.Clone` without options checks out the
  remote's default branch, so a module on `wotlk` (or `master` where the remote defaults to `main`) came back
  as different code with no mention of it. Force-replace now preserves the branch, and every re-clone reports
  which branch it landed on.
- **Re-cloning ignored the configured GitHub token**, while pull and fetch both used it — so a private fork
  failed to clone *after* its folder had already been moved aside.
- **A cancelled update left the realm down.** Cancelling during the pull loop, or any unanticipated exception,
  returned without restarting the servers that had been stopped for the update — even though no binary had
  been touched. Both paths now restore, like every deliberate failure path already did.
- **The "Update all" tab blanked the panel.** Its `IsSelected` binding also pushed `false` when the report was
  cleared at the start of a run, deselecting the tab without selecting another and driving the panel to empty
  for the duration. It's a trigger now, which reverts rather than forcing `false`.
- Modules without a git checkout are excluded from "Update all" but still compiled, so they could break the
  build while never appearing in the result. The batch now says how many were skipped and why that matters.

## [0.1.20] - 2026-07-19

### Fixed
- **The "Module repositories" settings card described the old behaviour.** It still said pins were "only
  needed for modules installed without git" — true until 0.1.17, which made them apply to git checkouts too.
  Anyone reading it would conclude the pin they needed wouldn't work. It now describes what pins are actually
  for, and what each kind of module does with one.
- The card's worked example pointed at a fork of mod-challenge-modes that deletes the semi-hardcore item-loss
  handler. It now names a fork that carries the compile fix without dropping features.

## [0.1.19] - 2026-07-19

### Added
- **"Replace modules that won't pull with the latest"** — a checkbox next to Update all + build, for a module
  stuck on old code that a pull can't reach.
  - A fast-forward pull refuses on a dirty tree or a diverged history, which is correct but leaves that module
    on its old code indefinitely. If the old code doesn't compile, it blocks *every* module, since AzerothCore
    builds them all into one target. There was no way out of that from inside the app.
  - When ticked, a refused pull re-clones the module from its own remote instead of skipping it. The existing
    folder is moved to `module-backups/` first, so local edits and commits are recoverable — but they no
    longer apply, and the confirmation says so in those words, with a warning icon rather than a question mark.
  - Off by default and deliberately **not** remembered between runs. A destructive option that quietly stays
    on would discard someone's work weeks after they forgot ticking it.
  - A module with no remote, or no git checkout at all, is refused rather than emptied — there'd be nothing to
    re-clone from, and moving the folder aside would destroy it for no gain.

## [0.1.18] - 2026-07-19

### Fixed
- **A failed batch build now names the modules that didn't pull.** "Update all" deliberately compiles on past
  a module it couldn't pull — one module with local edits shouldn't block nineteen others — but when the build
  then failed, the report said only `Build failed (exit 1)` and the pull failures were dropped from it. A
  module still sitting on its old code is the single most likely cause of the errors that follow, so the
  message now says which ones they are: *"mod-challenge-modes is still on the previous code after a failed
  pull — if the errors are in it, that's why."* Modules are listed by name, never counted; knowing two failed
  is useless without knowing which two.

## [0.1.17] - 2026-07-19

### Added
- **Module pins now work for git checkouts, not just ZIP installs.** Pinning a module to a fork used to change
  nothing if the folder was a real clone: the check reads the origin remote, and a pin it disagreed with was
  simply ignored. The Modules tab now says so — the Source column turns amber and names both repos — and
  offers a **"Use pinned repo"** button that repoints `origin` at the pinned fork and fetches.
  - The remote still drives the update check, deliberately. It's where a pull would genuinely fetch from, and
    reporting commits from a repo the checkout isn't wired to would be a lie. The pin is an intention; the
    remote is the fact. Surfacing the gap beats silently honouring either one.
  - Switching only rewrites the remote and fetches. Nothing is merged, reset or deleted — a fork almost always
    diverges from its upstream, and resetting onto it would throw away local commits.
  - When the histories *have* diverged (the normal state of a fork), no fast-forward can cross it. That's
    reported plainly, and moving across is offered as a separate, separately-confirmed re-clone, which keeps
    the old folder as a backup. `Reclone` grew a `replaceGitRepo` flag for exactly that case; it still refuses
    to touch a working checkout unless asked in those words.
  - A failed fetch puts the original remote back. A module pointed at a URL that can't be reached is worse
    than one pointed at the repo it started on.

## [0.1.16] - 2026-07-19

### Added
- **"Update all + build" on the Modules tab.** Pulls every git-backed module, then compiles and deploys once.
  - Repeating the per-row "Pull + Build" was the only way to update more than one module, and it was the wrong
    shape: each run shuts the servers down, backs up the databases and recompiles the whole tree. Twenty
    modules meant twenty full rebuilds of code that AzerothCore compiles into a single target anyway — and,
    with the cmake review window on by default, twenty prompts to click through.
  - A module that can't be pulled (local edits, diverged history, a ZIP install with no remote) no longer
    stops the others. It stays at its current commit, which is a perfectly buildable state, and is named in
    the result. The run is only abandoned if *every* pull failed, since then there's nothing new to compile.
  - Each module's pull outcome lands on its own row, and the batch's compiler errors get their own "Update
    all" tab — a build covering every module doesn't belong under any single one. That tab comes to the front
    by itself when a build fails.

### Changed
- The build-report panel is now one view (`BuildReportView`) shared by a module row and the batch, rather than
  a block of XAML bound to a single row's properties. The batch needed exactly the same panel.

## [0.1.15] - 2026-07-17

### Added
- **Players and bots on the World card.** How many characters are actually in the world, split into real
  players and playerbots. Bots are identified the way mod-playerbots identifies them itself — every random bot
  lives on an account whose name starts with `AiPlayerbot.RandomBotAccountPrefix` (default "rndbot"), read
  from the module's own conf rather than assumed.
  - This reads the database directly (a new MySqlConnector dependency). The world server's console could have
    been asked instead, but its command replies go to stdout, which AzerothCore never flushes once captured —
    an answer might arrive minutes later or never. The database is the only honest source.
  - Accounts live in the login database and characters in the character database, so the count joins across
    the two. That needed each connection to be read by its config KEY; the existing detection flattens every
    `*DatabaseInfo` into one list, which is all a backup needs but can't tell auth from characters.
  - It's read-only, throttled to once every 5 seconds, never overlapped, runs off the UI thread with a
    5-second timeout, and shows "—" rather than failing if MySQL is down or the server is stopped. The rows
    appear on the World card only — authserver has no characters in it.

## [0.1.14] - 2026-07-17

### Changed
- **Layout pass across the app.** Page padding was 8, 12 or 16 depending on the tab, and vertical gaps used
  4/6/8/10/12/14/16 with no pattern — the thing that makes a layout read as "almost aligned". There's now one
  spacing scale, applied everywhere: 12 between blocks, 8 between tightly-related items. Cards no longer carry
  their own margin (a card shouldn't decide how far it sits from its neighbours; its container should), which
  is what made padding and margins compound differently on every tab.
- **Settings is grouped into cards, and the save bar is docked.** It was one flat list of ~20 fields whose six
  sections sat at identical visual depth, with Save at the bottom of a long scroll — so saving the field you
  just edited meant scrolling to find the button. Save and Auto-detect are now always visible.
- **The header shows live World/Auth status.** Whether the realm is up is the question this app exists to
  answer, and it was only visible on the Dashboard tab.
- **The dashboard tiles are genuinely equal thirds.** They were laid out in a UniformGrid, which gives every
  child the same width and consumes each child's margin *inside* it — so adding gaps made two tiles narrower
  than the third. The gaps are now real columns.

### Added
- **"Include the server's config folder in backups" is now a setting you can see.** It existed and was on by
  default, but had no UI — it could only be changed by hand-editing settings.json.

## [0.1.13] - 2026-07-17

### Fixed
- **The Auth console showed no server output.** Not a config problem, and not the console pane: AzerothCore's
  console appender never calls `fflush`, so as soon as its stdout is captured through a pipe the C runtime
  switches to full buffering (4KB). worldserver is loud enough to keep filling that buffer; authserver emits a
  few hundred bytes at startup and then almost nothing, so its output could sit undelivered indefinitely — the
  pane looked broken while the server was perfectly healthy. Its FILE appender flushes every line, so the
  consoles now follow the server's log file, whose path is read from the .conf the same way the server reads
  it (`LogsDir`, `Logger.root`, and the appender's timestamp flag). stdout is ignored while tailing, or the
  same lines would arrive later in 4KB gluts and duplicate everything.
  - The earlier hint blaming `Logger.root` was wrong — the stock config enables the Console appender — and has
    been replaced with the real explanation.

### Added
- **More on the server cards.** The **listening port**, read from the .conf rather than assumed, so a
  non-default port shows the real number — the first thing to check when clients can't connect. The **PID**,
  for Task Manager, netstat, or a crash dump. And the supervisor's **last event**, which is where the crash
  reason ("Crashed — FATAL: cannot connect to database") now lives; it was previously console-only, where it
  scrolls away.

### Changed
- The two dashboard cards are now one control used twice. They were identical markup duplicated, so every
  change had to be made in both places and could drift.

## [0.1.12] - 2026-07-17

### Added
- **Pin a module to a specific repo.** Modules installed without git are identified by matching the folder
  name against the catalogue, which finds the *popular* upstream — wrong for anyone deliberately running a
  fork, and it would have offered to re-clone over their fixed version with the one they left. Settings now
  has a Module repositories list mapping a folder to a repo (`owner/repo` or a URL), which overrides the
  guess. Git checkouts are unaffected: their `origin` is a fact and is always believed over any guess.

### Fixed
- **The module action buttons were cut off.** The grid carried eight columns whose minimum widths totalled
  more than the window can be narrowed to, so the columns overflowed and Pull / Pull + Build / Re-clone were
  clipped at the edge. Branch, Local and Remote — three columns saying one thing — are now a single Revision
  column reading `master  a119a6d → 93aaea3`, the remaining widths are trimmed, and horizontal scrolling is
  available so the buttons can never become unreachable even on a narrow window.
- **"No modules folder found" now names the path it checked.** The old message gave no way to tell a wrong
  setting from a broken app — including the case where the folder had simply been deleted. It now reports the
  path, or that the Source directory is unset or missing.
- **The modules folder is found even when the Source directory is blank or off by one.** The run directory
  normally sits inside the source tree (`<source>/env/dist/bin`), so `modules/` is one of its ancestors and
  can be recovered from there. A Source directory pointed straight at `modules/`, or one level above the
  source tree, now also resolves instead of failing.
- **"Auto-detect paths" no longer refuses to fill an empty box.** It assigned with `??=`, which only replaces
  a null — but a text box the user has cleared holds an empty string, so the one button meant to repair a
  blank path did nothing to it.

## [0.1.11] - 2026-07-17

### Fixed
- **The console stopped following new lines after leaving and re-entering its tab** (a regression introduced
  in 0.1.10). Auto-scroll released its pin whenever a scroll arrived with the extent unchanged, treating that
  as "the user scrolled away" — but re-showing a tab re-measures the list and fires exactly that, with the
  offset still at 0, so returning to the Console tab looked like a deliberate scroll to the top and unpinned
  it for good. Output kept arriving below the fold, so the console appeared frozen. Releasing the pin now
  additionally requires the viewport to be unchanged; a viewport change re-pins to the newest line.

### Added
- **A Configs tab.** Edit the server's .conf files in the app: core configs and module ones, listed with
  their folder so same-named files are distinguishable, in a monospace editor. Every save keeps a timestamped
  `.bak` of the previous contents beside the file, writes no BOM (which can make AzerothCore's parser choke
  on the first setting), and passes the text through verbatim — comments, blank lines, and CRLF included.
  `.conf.dist` templates are deliberately not offered: editing one changes nothing the server reads.
- **Backups now include the config folder.** The whole config folder from the run directory goes into the
  archive under `config/`, `modules/` subfolder and all — a database restored without the config it was
  running under is only half a recovery. Where the .conf files sit loose beside the binaries there is no such
  folder, so only the .conf files are taken (copying the run directory would sweep in the entire install).
  Can be turned off in settings.
- **Modules installed from a ZIP are now identified via the AzerothCore catalogue.** The catalogue is a
  GitHub topic search (`azerothcore-module`) whose repo names match module folder names, so a module with no
  git checkout can still be traced to its upstream. Those rows now show the real repo in a new Source column
  and the latest release tag, rather than a dead-end "not a git repository".
- **Re-clone.** A module identified this way can be replaced with a proper git checkout in one click, which
  is what enables update checking for it. The existing folder is moved aside as `<name>.backup-<timestamp>`
  and never deleted — it may hold local edits — and if the clone fails the original is put back. It confirms
  first, and notes that a rebuild is needed since re-cloning brings in the newest upstream code.
- **An empty console now explains itself** instead of showing a blank box, and both consoles carry server
  lifecycle events ("Auth Server started."). authserver only writes to stdout when its conf enables the
  Console appender, so its pane could sit empty while the server was perfectly healthy — indistinguishable
  from a broken app. The hint names the `Logger.root` setting responsible.

### Changed
- **The window title bar is dark**, matching the app rather than sitting on it as a strip of light OS chrome.
  On Windows 11 the caption, border, and text are painted in the app's own gunmetal; on Windows 10 it falls
  back to the standard dark caption.

## [0.1.10] - 2026-07-16

### Fixed
- **Modules installed from a ZIP were missing from the Modules tab.** The list only included directories that
  were git repositories, so any module installed by unzipping a GitHub download was dropped silently, with
  nothing to explain the omission. Every directory under `modules/` is now listed; ones that aren't git
  checkouts say so in the Status column instead of disappearing, and the summary line no longer reports
  "all up to date" while quietly ignoring them.
- **"Start all" could kill a running world server.** The orphan cleanup that frees a held port matches purely
  on executable path, so it could not tell a live supervised server from a leftover — and it ran *before*
  the start, which then silently no-opped because the server was already running. With world up and auth
  down, "Start all" killed worldserver outright: no `.server shutdown`, no player warning, no character
  save, followed by a watchdog "crash" restart. Cleanup now only runs when the server is actually down.
- **Cancelling an update force-killed the world server mid-save.** A cancelled wait was indistinguishable
  from a timed-out graceful drain, and a timeout means "kill it" — so pressing Cancel produced the most
  destructive outcome available. Cancelling now aborts the wait and leaves the server to finish draining.
- **Quitting orphaned the servers.** `OnExit` was `async void`, so it returned to WPF at the first await and
  the Dispatcher tore down before the cleanup that kills the child processes ran. worldserver/authserver
  were left running headless holding ports 3724/8085 — which is why launches so often reported cleaning up
  "orphaned" processes. Shutdown cleanup is now synchronous and bounded.
- **A failed update left the realm down.** A failure during backup, pull, or build returned without bringing
  the servers back, even though nothing had been deployed and the installed binaries still worked.
- **Updating with the world server already stopped took authserver down for good.** Shutdown stopped both
  servers if *either* was running, but the restart only keyed off the world server — so authserver stayed
  down under a report that said "Update complete." The mirror case also started a server the user had
  deliberately stopped. Each server is now tracked independently: exactly what was stopped is restarted.
- **Binaries could be deployed under live servers.** Shutdown was gated on the caller asking for a rebuild,
  but the build/deploy ran whenever the pull *recommended* one — overwriting a running .exe. The shutdown is
  now re-checked at the point the rebuild is decided.
- **One network blip permanently disabled auto-update** and later popped a raw stack-trace dialog. Octokit
  reports connectivity failures as `HttpRequestException`, which was not caught, so it faulted the
  fire-and-forget check loop for the rest of the session and resurfaced at a random GC as an
  unobserved-exception message box. Also, the "no latest release" fallback ran *inside* a catch block, where
  sibling catches don't apply, so a typo'd repo name escaped the same way.
- **The crash-loop breaker could trip on the first crash after a manual start.** Crash counters survived a
  Start, so after fixing the cause (e.g. bringing MySQL back) an unrelated crash minutes later could trip
  the breaker immediately, and the restart backoff resumed at minutes instead of seconds.
- **Crash notifications could quote the previous process's error.** The recent-output buffer was never
  cleared on relaunch, so a process that died silently was blamed on the *old* process's last error.
- **One collision with the UI thread silently killed the scheduler.** Ticks enumerated the same job list the
  UI mutates, and the loop caught only cancellation — so adding a job during a tick faulted the loop and
  every scheduled backup and restart stopped firing until the app was restarted, with nothing shown.
- **Scheduled jobs could be skipped entirely.** Jobs fired only if a tick landed inside their exact minute,
  but ticks are collapsed while a job runs — so a 6-minute backup starting at 03:00 ate a 03:05 restart's
  only chance to fire. Jobs now run within a 10-minute catch-up window.
- **The Pull button stayed enabled during a build,** letting a `git pull` run against the source tree the
  compiler was reading.
- **An unreadable .conf crashed path auto-detection** with a raw stack-trace dialog.
- **The console could stall and fall behind during heavy output.** The flush tick and the auto-scroll both
  ran at `DispatcherPriority.Background` — below Input — so they were starved by the very layout storm they
  needed to keep up with. Auto-scroll also measured "near the bottom" in a 48-unit slack against a list that
  scrolls in *item* units, not pixels, so reading a few lines up still snapped you to the end. Auto-scroll
  now follows the scroll viewer's own extent changes, and releases only when you actually scroll away.
- **A malformed colour in the theme** (`#FF31384252` — ten hex digits) sat unreferenced; the first use would
  have thrown at window construction.

### Changed
- **Dark gunmetal grey theme.** A single neutral gunmetal ramp with no blue cast. Cards now sit on a darker
  canvas instead of sharing the tab background, which previously made them invisible except for a hairline
  border. Tooltips are themed rather than falling back to the system's pale-yellow box.
- **Default window is now 1280×840** (minimum 1040×660). The three dashboard cards were fixed at 316px and
  only just fit the old 1060 default; below it the third wrapped onto a line of its own. They are now equal
  thirds that scale with the window.
- **Schedules list no longer overflows the tab** — the grid was in a StackPanel, which gave it unbounded
  height and no scrollbar.

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
