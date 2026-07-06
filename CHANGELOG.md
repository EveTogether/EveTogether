# Changelog

All notable changes to the EVE Together desktop client are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Releases are built by the GitHub Actions release pipeline (`.github/workflows/release.yml`):
publishing a GitHub Release tagged `vX.Y.Z` attaches self-contained binaries for Windows
(`.zip`), Linux (`.tar.gz` + `.AppImage`) and macOS (`.zip`, arm64 + x64). The notes for a
release are taken from the matching `## vX.Y.Z` section below.

## [Unreleased]

## v0.2.0-beta — 2026-07-06

First beta of the EVE Together desktop client, and the first release from the public
repository. **Beta:** more stable than the alpha, but expect occasional rough edges.

### Added
- A Copy button on each row of the App Logs window puts that entry on the clipboard — the full
  timestamp, level, logger category, message and (when present) the exception — so an error can be
  pasted straight into a report or message without scrolling back to find it.
- The main window now remembers its placement between sessions — width (per layout mode),
  height, position and maximized state are restored on the next launch. A saved position is
  only restored if it still lands on a connected monitor; otherwise the window re-centres on
  an available screen (so a removed/rearranged monitor can't strand it off-screen).
- Reorder the character list by dragging a character card; the order is saved and reused
  everywhere the characters are listed (metrics, pickers).
- Delete individual inbox messages, and clear the whole inbox at once (with a confirmation).
- Click a DPS overlay to bring that character's EVE client to the front (Windows / Linux).
- A confirmation prompt when closing the app while pop-out windows are still open, with a
  "don't ask again" option.
- An About dialog (rail → About): app version, the creators with their EVE portraits, the
  projects that inspired the app (eveship.fit, pyfa, EVE Workbench), the AGPLv3 license and
  source link, and the required CCP attribution.
- Rename a wing or squad from the roster (right-click → Rename); for a fleet coupled to your
  live in-game fleet the rename is also applied in EVE (when you hold the in-game write access).
- Pushing your fleet structure to a coupled in-game fleet now also removes wings/squads you've
  taken out of your plan from the EVE fleet — after a confirmation listing exactly what will go.
  Only empty units are removed, members are kept, and EVE's default Wing 1 / Squad 1 are left alone.
- Uncouple a fleet from its live in-game fleet (roster → UNCOUPLE): clears the stored link so the
  app stops driving and polling EVE for it. This also happens automatically once the in-game fleet
  has been gone for a few polls (dissolved or re-formed), so a server fleet no longer keeps a dead
  in-game link that other clients would still poll.
- Two hands-off toggles on a coupled fleet's manage band (need in-game write access). **Auto apply
  structure** pushes a newly added wing or squad to your live EVE fleet the moment you create it, so
  you don't have to press PUSH STRUCTURE — removing units still goes through that button's confirmation.
  **Auto invite members** sends an in-game invite to a pilot the moment you drop them into a wing/squad,
  skipping anyone already in the fleet. Both settings are remembered per fleet.
- Leave a fleet with one of your characters straight from the roster window, not just from the fleet
  overview. When you're multiboxing several of your characters into the same fleet, a picker lets you
  choose which ones to pull out; your owning character is never offered (it disbands or transfers instead).

### Changed
- The home landing is now a dashboard that shows only your own data, replacing the old landing
  that listed the live DPS of every connected client (a privacy leak on a busy server). It has
  stat tiles (characters in EVE, active/forming fleets, shared fits, server connection), a live
  ISK-today total, your characters with their portrait, in-EVE/offline state, location and live
  DPS, the fleets you own or fly in (flagged "you own it"), the latest fits shared on your
  servers (hull icon, which server, and how long ago it was shared) and recent activity. The
  "fits shared today" tile now counts only fits shared today. The in-EVE count and presence update
  live as EVE clients start and stop. Your other characters that have no live combat yet appear as
  greyed "offline" rows, and a fleet's doctrine name is shown next to its member count. The fleets
  and shared-fits cards refresh on their own when a fleet changes or a fit is shared, instead of
  waiting for a manual refresh. A character that is in EVE but not yet in combat now shows as a
  normal row with its current system (updated live as it jumps) and an empty graph until combat
  starts — only genuinely offline characters are greyed.
- Inbox messages now show when they were sent, and a fleet action delivered to several of
  your characters is merged into a single entry listing every recipient — no more duplicate
  messages per character when multiboxing.
- Pop-out windows (DPS overlays and floating modules) are now independent of the main
  window: minimizing the main window no longer minimizes them too.
- Reworked the settings screen into a categorised layout — a category list (General,
  Interface, Privacy & Sharing, Integrations) on the left and the matching settings on the
  right — and it now opens as a docked tab in docked mode instead of a separate window.
- Polished the fleet composition cards: a uniform card background (no more dark banding
  behind the ships and tags) and hexagonal ship icons matching the character portraits.
- The fleet composition detail/edit view now shows ship hulls in the same faction hexagon
  as the overview, the fit rows are aligned, and the "per-fit min" field is now "min. needed".
- The Fleets window is now one overview grouped per server (with a Local fleets section)
  instead of the separate Browser / Participating / My Fleets / Local tabs. Each fleet shows
  where it lives and its owner, and only the actions your relationship allows: manage / edit /
  disband as the owner, a read-only view of the structure and assigned fits once you've joined,
  or join / request otherwise.
- In a fleet's member list you can click a member's fit to open its detail; join and add-toon
  let you pick several characters at once; join / request stay visible (disabled) when every
  one of your characters is already in; a "?" marks a pilot whose can-fly status is unknown;
  and local fleets now list their members (fit, can-fly, select fit) just like server fleets.
- When a fleet is coupled to your live in-game fleet, moving a member to another wing/squad or
  removing them now also moves or kicks them in EVE (when you hold the in-game fleet's write
  access), so the EVE fleet follows what you do in EVE Together. A swap is not mirrored.
- The app log window now keeps warnings next to errors (warnings tinted amber, errors red), and
  ESI "not found" replies such as "character is not in a fleet" — a normal state — are logged as
  warnings instead of errors, so they stay visible for diagnosis without crowding the error list.
- JOIN and REQUEST now also appear on a fleet you already own or are flying in, so you can bring
  another of your characters along — they used to be hidden the moment you were involved.
- Each of your characters in a fleet now has its own LEAVE on the fleet overview, so you can pull one
  character out while your others stay in — useful when you've multiboxed several into the same fleet.

### Fixed
- Saving an ESI token no longer fails intermittently on Windows when two saves for the same
  character overlap, or a save lands while the token file is being read — the file replace now
  retries briefly instead of giving up.
- A local (client-only) fleet now feeds the live graphs of every one of your characters in it, not
  just the one that created it. When multiboxing several characters into a local fleet, the metrics
  window showed DPS, cap, bounty and location for the fleet leader alone while the rest stayed blank;
  all members are now tracked, the same as a server fleet.
- A character's system on the home dashboard now updates every time it jumps, not only when its
  gamelog is first picked up — so an online character's location stays current as it moves around.
- A server connection now recovers on its own after the server restarts (or its connection otherwise
  wedges) instead of getting stuck until a client restart: it stays alive while idle, and when reconnects
  keep failing the app rebuilds the connection from scratch automatically. The reconnect attempts are now
  shown in the log window instead of failing silently.
- Pop-out windows now show the EVE Together icon in the taskbar instead of the default icon.
- After a fleet is uncoupled from its live in-game fleet, the app no longer keeps asking EVE
  about your characters' in-game fleet — that check is now only made for characters that are a
  (non-boss) member of a coupled fleet, so an uncoupled fleet stops generating ESI traffic and
  "not in a fleet" log lines. A dissolved in-game fleet is also detected one poll sooner.
- Assigning a pilot to a wing/squad on a coupled fleet now checks who is actually in the live EVE
  fleet first: a pilot already in it is moved to the position, a pilot who isn't yet is sent an
  in-game invite there — no more "Cannot move non-member" error (and wasted ESI call) for a pilot
  who hasn't joined. Inviting from the member list / "Invite here" now also sends the real in-game
  invite, not just an internal one.
- A routine ESI "not found" response (e.g. "character is not in a fleet") is now written to the log
  once instead of twice.
- The recurring "character is not in a fleet" check that runs while you're a member of a coupled fleet
  but not yet in the EVE fleet no longer shows up in the log window: that one expected outcome is now
  logged at a quieter level, while a boss-side "fleet gone" 404 still stays visible.
- A coupled fleet that has really disappeared in EVE but that ESI keeps answering with a server error
  (500) instead of "not found" is now uncoupled too, after a sustained run of failed checks, instead of
  being polled forever — a brief outage with the occasional good check still keeps the link. Only EVE-side
  failures count toward this: a local problem (re-auth needed, no connection) never uncouples a fleet.
- When your computer's clock has drifted, the app no longer floods the log with errors every few seconds
  while ESI tokens briefly look expired. It now notes the situation once, backs off instead of hammering
  EVE's login server, and recovers on its own once the clock is corrected or the token is refreshed.
- Live fleet metrics from every one of your characters now reach the fleet. Previously, when you ran
  several characters on one machine, only one character's DPS, bounty and location came through and the
  others were silently rejected by the server — so the rest of your characters showed up blank. Each
  character now publishes over its own connection, so everyone sees all of them.
- Opening MANAGE on a second fleet while a roster window is already open now shows that fleet's roster
  instead of staying stuck on the first one — each fleet gets its own roster window.
- A fleet you set up in advance is no longer auto-archived while it waits. Only a fleet you've concluded
  gets cleaned up, so a fleet planned days ahead stays open for people to sign up and pick fits until you
  fly it — you (the owner) or the server admin still remove it whenever you want.

## v0.1.0-alpha — 2026-06-13

First public alpha of the EVE Together desktop client — a local-first, cross-platform
(Windows / Linux / macOS) companion for EVE Online. **Alpha:** expect rough edges; data
and settings may not survive future versions.

### Added
- **Live game-log tracking** — per-character DPS (in/out), mining, bounty and location
  read straight from the EVE game logs, with smooth live graphs, pop-out overlays and a
  per-character metrics window.
- **Fittings** — import from EFT / DNA / eveship.fit, export back, a fit browser (hull,
  slots, price, tags) and a fit-detail view with a dogma-based stat simulator (resists,
  EHP, CPU/PG, slot layout, drones, special holds, damage profile, weather/environment).
- **SDE store** — a local, auto-updating EVE Static Data store powering type/skill lookups.
- **Fleets** — create and manage fleets with a wing/squad roster, reusable fleet
  compositions (doctrines), per-member fit assignment and cross-client can-fly badges.
- **ESI integration** — PKCE sign-in, skills, implants, portraits and market prices.
- **Optional self-hosted server** — couple characters to a server (gRPC, TOFU-pinned TLS)
  to share fits, fleets and compositions and view live fleet metrics together.
- **Optional local widget API** — a loopback HTTP/WebSocket server for OBS/Twitch overlays.
- **Local EVE-client presence** — the character list shows which characters have a running
  EVE client on this machine.
- **EVE-styled UI** — borderless chrome with live faction theming (Amarr / Caldari /
  Gallente / Minmatar), docked or floating module shell.
- Cross-platform GitHub Actions release pipeline: self-contained single-file builds for
  Windows / Linux / macOS, attached to each published GitHub Release.
