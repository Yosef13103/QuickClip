# QuickClip P0 — installed-game discovery and explicit application capture scope

**Updated requirement:** 2026-11-11. Integrates with PR #4 and current `.ai/public/P0_SPRINT_CONTRACT.md`. Read this before implementing new Games UI or changing catalog runtime rules.

## What the user wants
The Games settings should be analogous to SteelSeries GG's useful flow: **Automatic game discovery**, list all detected titles, immediate **Enable capture / Disable capture**, **Scan now**, **Add Game**, and optionally game-specific settings that actually work. But discovery must not depend on guesses from names or on the owner having to manually point the tool at every install.

### Default sources — recognized launcher installs, not a blind disk walk
At first launch show a clear list of recommended auto-detected libraries, the evidence path, an enable/disable scan switch per source and a summary of what will be read. Allow users to enable automatic scanning of recognized launchers; honor privacy selection. Prefer:
- **Steam:** detect Steam installation via registry/known locations, read `config/libraryfolders.vdf`, enumerate per-library `steamapps/appmanifest_*.acf` and installed locations, including alternate drives.
- **Epic Games:** resolve launcher-known location and installed-game manifest metadata such as `%ProgramData%/Epic/EpicGamesLauncher/Data/Manifests/*.item`. Do not assume a game is installed solely because `C:/Epic Games/` exists.
- **Battle.net:** investigate supported read-only launcher installation records (product database/config/registry), with version-tolerant parsers and error reporting.
- **EA App / Origin**, **Ubisoft Connect**, **GOG Galaxy**, **Riot**, **Xbox / Microsoft Store / Game Pass**: discover supported installed-game records from documented or inspectable launcher config/registrations, package registration and known install roots. **Do not promise these scanners are already implemented**. Only mark a launcher supported once its parser, package handling and tests are present. Some stores use registry/package records instead of straightforward folders or ordinary accessible EXEs.
- Show supported, unimplemented and inaccessible launchers separately. Missing launchers do not cause errors or empty known games. Do not synthesize fake detected games from display names or `*.exe` alone.
- Common names (e.g. `steamapps`, `Epic Games`, `XboxGames`, `Riot Games`) are **candidate directories**, not verified installation identities. Read launcher metadata where available and use process ownership/actual executable matching before automatic capture.

### Custom sources and precise control
User may:
1. Add a launcher library root or a custom folder, e.g. `D:/FreeWare/`, optionally including bounded subfolders, with opt-in scope and clear recursion limits.
2. Add **one exact executable** (`D:/FreeWare/MyGame/MyGame.exe`), select other allowed executables/launchers associated with the same entry and set a friendly name.
3. Add **path-scoped glob patterns** such as `D:/FreeWare/*/bin/*-Win64-Shipping.exe`, with preview of matches, finite glob/path recursion budget, and manual confirmation of matches. No unscoped global `*.exe` scan or untrusted regex evaluation; no automatic capture just because some unrelated process has that basename.
4. Enable/disable discovered or manual games, exclude folders and executables, edit names and game associations, and safely remove manually added entries. All decisions survive restart and interrupted/partial scans. Explain "Disable capture" precisely in relation to explicit whole-display capture.
5. Optionally review unknown foreground executables and choose whether to classify a discovered item as **Game**, **Application/companion**, **Launcher**, or **Ignored**. Never silently promote non-games. Prefer users' explicit overrides over guessed metadata, while keeping dangerous global patterns out of automatic capture decisions.

### Discord special case — important
The user historically had to manually add Discord's `Update.exe` to SteelSeries Moments to capture Discord. That is a **user request to optionally capture an application**, not evidence that every process named `Update.exe` is Discord, and not a reason to misclassify Discord as a game.
- Preserve default Discord classification as **companion/non-game**, including audio/chat routing, stream/video pop-outs and process descendants.
- Plan a distinct **Capture Applications** / manual target opt-in (possibly in a future Games subtab): allow selecting a **specific installed Discord executable or launcher path** and its known child process/window identity; the rule is path-/owner-scoped, not global basename `Update.exe`.
- If the user chooses a verified Discord application window as the *only* explicit target, capture only with an authorized backend/capability and explain WGC borders on unsupported Windows; not automatically alongside a game.
- Capturing **Discord pop-out video simultaneously with gameplay** requires a second independently approved visual source, UI/source selection and synchronization. This is NOT delivered merely by adding a `Update.exe` rule, nor is it part of this P0 integration unless a separate confirmed implementation exists. Do not claim it works today.
- Respect Discord privacy: no automatic video capture of calls, shared streams or pop-out windows without explicit source selection.

## Product UI
Settings has a dedicated Games page/tab/submenu (not an ever-growing General form):
- Top: `Automatically discover installed games` [toggle] / `Scan Now` / `Add Game`.
- A secondary `Game libraries and launchers` panel lists auto-detected launcher installs + their detected roots and support status, plus custom approved folders and manual scoped patterns.
- Main: virtualized/searchable Games list, count, source badges (Steam/Epic/Custom/etc.), executable identity, `Capture enabled / Disabled / Needs review` and a compact toggle.
- Details: what matched and why, approved exe, global-default versus supported per-game override, optional aliases and exclusions, preview scan matches, edit/remove. Only show effective controls when the underlying recorder obeys them.
- User-facing errors: inaccessible path, partial scan, launcher not supported, ambiguous match, disabled auto-capture; don't silently broaden scope or delete previous identities on scan failure.
- Keep scan memory/time bounded, cancellable, rate-limited, and asynchronous so UI and recording don't stutter. Keep game identity stable on launcher exit, app update, offline library and PID reuse.
- Provide tests for Steam main/alternate libraries; Epic manifest; absent/invalid manifests; uninstalled launchers; offline roots; `D:/FreeWare/` folder; exact EXE; path-glob preview; `Update.exe` collisions; disabled game after partial scan; and gameplay → Discord foreground → game transitions. For unsupported launchers, document blocked requirements rather than fabricated green scores.

## Implementation constraints
The currently completed PR #4 implements scoped Steam/folder discovery and manual exact EXEs. **It does NOT claim production-ready Epic/Battle.net/EA/Ubisoft/GOG/Riot/Xbox discovery**, or arbitrary safe path-glob support. Chat 4 should complete integration and fixes without disguising these gaps; Orchestrator must schedule incremental launcher-scanner additions to **Chat 3** with separate tests/PRs and accepted evidence. Do not casually turn the current P0 integration into an unlimited rewrite of every launcher parser. Implement only the scanner additions that can be proven safe and tested within a bounded work item.

Source references (illustrative; verify actual current launcher formats):
- Valve Steam Support: https://help.steampowered.com/en/faqs/view/4578-18A7-C819-8620
- Epic Games support: https://www.epicgames.com/help/c-32735058/c-37477814/a11960797
- Third-party implementation examples are exploratory, not authoritative specifications; validate schema and permissions with fixtures and installed launcher versions.
