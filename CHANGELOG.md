# Changelog

All notable changes to the EVE Together desktop client are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Releases are built by the GitHub Actions release pipeline (`.github/workflows/release.yml`):
publishing a GitHub Release tagged `vX.Y.Z` attaches a self-contained Velopack build for Windows
(`-Setup.exe` + `-Portable.zip`), Linux (`.AppImage`) and macOS (`-Setup.pkg`, arm64 + x64) along
with the update feed those installs read, and publishes the
server as a Docker image to `ghcr.io/evetogether/eve-together-server` — tagged both `:latest`
(always the newest build) and with the release version (`:X.Y.Z`). The notes for a release are
taken from the matching `## vX.Y.Z` section below.

## [Unreleased]

- **A restricted site's allowed ships are now shown grouped by ship group, not as a wall of loose hull names.**
  ACTIVITY's SHIPS row reads "Cruisers (T1 only)" for a homefront whose individually-named allow-list is exactly
  every published Tech I hull of that group, "Cruisers (except Slasher)" when only a couple of hulls are carved out
  of an otherwise-whole group, or the honest "Cruisers (some hulls only)" when neither can be said concisely — the
  actual hull names are then a hover away rather than crowding the row itself. An excluded hull is still never shown
  as allowed. The row now stands inline instead of behind a "which ships may enter?" link, since a grouped label is
  short enough not to need one.
- **CONSUMABLES now proposes a filament count for a frigate (3) and a cruiser (1), not only a destroyer (2).**
  Source: Jithran, 2026-09-12 — the SDE does not carry this either, the same finding ET-249 made for the destroyer.
- **Homefronts have a HOMEFRONT section: who was in the site when it completed.** A homefront pays a fixed amount
  to every character counted in the site, and how much depends on how many were counted — not on the fleet's size.
  The run window now lists every character on the fleet's roster in one flat list, your own characters marked
  "Local", each with a tick for "in site at completion" and the reason it was proposed: damage or remote repair
  dealt, remote capacitor given, a wreck salvaged, ore mined, or "had activity this run" for another pilot's
  character. A character with nothing logged starts unticked and is marked, so the hauler outside the site stands
  out; an external pilot starts unticked too, since no evidence can ever arrive for them. Pilots in the site who are
  on no roster at all are counted with "+ pilots not on the roster", and "6 in fleet · 5 in site" is shown as you go.
  In a fleet the fleet commander decides: every member sees the commander's list read-only and has it written on
  their own runs. Flying without a fleet, you decide yourself. Homefronts flown in a row start from the list the last
  homefront in the same fleet ended with; evidence can add a tick to it, never take one away. The saved activity shows
  who was counted, who decided and when, and the fleet's size at STOP — and the one who decided can still change the
  list there, which reaches every member again, also one who only comes online later. Online/offline beside each name
  is the live fleet view and is never stored.
- **A homefront's payout: a fixed amount per ticked character, from a table by N — never from your wallet.** HOMEFRONT
  now asks how the site ended: completed, failed or unknown, picked at STOP; Abyssal Artifact Recovery asks how many
  of its 9 waves paid instead, since a site that fails part-way keeps whatever it already cleared. Once completed,
  every character ticked "in site" shows the table's own fixed amount for N — Hall of Sacrifice at 5 pays 15,000,000
  ISK each, at 4 it is 12,000,000, at 6 it is 11,250,000; Metaliminal Meteoroid at 5 pays 12,000,000 — read next to
  the site's own kind and marked "expected" everywhere it appears: the row, the HOMEFRONT header, TOTAL ISK, and the
  runs overview's PAYOUT chip. It stays expected until you say what actually arrived — confirm the table's own figure
  in one click, or type what you received — one character at a time or all your own at once with "CONFIRM ALL AS
  EXPECTED". The saved activity also names which table version it was priced against, so two clients on two app
  versions can never silently disagree about the same site. Nothing about this ever reads, derives or asks for wallet
  access. Only the 19 Mar 2026 table is built in; a homefront flown before that date shows no expected payout at all,
  since no measured table exists yet for what CCP paid before it, rather than pricing an old site off today's numbers.
- **A Metaliminal Meteoroid now marks itself completed on its own.** The moment the site's asteroid runs dry, HOMEFRONT
  reads "completed · from the game log" — the fleet commander or pilot can still change it by hand, and doing so
  replaces the automatic tag outright. Every other homefront kind still asks. This is caught up too if it happened
  while the app was closed: resuming a run picks it up the same way it already catches up bounty, mining and enemies.
- **Mining now travels with a fleet run when it is published.** A fleetmate's mining was left out of what their run
  sent to the server, so a synced group showed only your own ore.
- **A mission flown with more than one own toon no longer counts its reward twice.** Before, every own toon in the
  group — the shooter and a salvage alt riding along on the same fleet — was written with an identical copy of the
  same ISK, bonus and LP reward, so the runs overview's reward chips, and the MISSION section on the saved detail
  screen, both read double what the mission actually paid. Now only the character whose own clipboard copy accepted
  the mission gets the reward; the MISSION section also says which character it belongs to. A one-time repair
  corrects any mission group already saved with the duplicate, keeping the reward on whichever run has the most
  bounty. A mission flown with a single character is unaffected.
- **The homefront type repair now also reaches runs recorded before the site catalogue knew them, and runs a second
  time after an EVE static data update.** Before, a run started while its homefront was not yet in the site
  catalogue stayed an "ordinary site" forever, even once an exact name match became available — and on the very
  first startup after a static-data update, the repair ran before that update had actually finished downloading, so
  it fixed nothing until the app was restarted a second time. Now an exact homefront name repairs a run regardless
  of which of those two states it was started in, and the repair also runs again once that same session's static
  data update finishes.
- **ENEMIES now keeps collecting for every character in an own-toon group after a run window is reopened or
  resumed.** Before, only the run a window was FRESHLY started or joined for ever got its own ENEMIES collector — a
  sibling character (ET-210's own-toon group) discovered later, because the window instead adopted an
  already-running group (RESUME after a crash or closed app, ET-254/258, or simply reopening a run window that was
  closed while the group kept going), never had one created for them at all. Their own combat then had nowhere to
  go for the rest of that window's life, live or on SAVE — while bounty and loot, which never depended on this
  per-window wiring, kept working regardless, which is why a run could show 118 correct bounty payouts and zero
  enemies. Now every participant discovered in a run's group — not only the one the window was pointed at — gets its
  own collector the moment the window learns about them.
- **Solo runs no longer get warned about a fleet that isn't there.** Before, starting any run put "no other member
  has reported in yet" in the title bar even when flying alone, and a run started while merely a member of a
  long-forgotten personal fleet — never started, never joined by anyone else — said "'Local HF' has not been
  started, so this run is not shared" every time. Now the run window only mentions a fleet when there is one to
  mention: the "not shared" notice only fires for a forming fleet somebody else is actually signed up to, and the
  header chip only reports on fleet members once a real member has actually reported in. A pilot who forgot to
  start a fleet they are genuinely flying with — or who is in several started fleets at once — still gets told,
  unchanged.
- **SAVE while a run is still going.** Before, ending and saving a run always took two clicks — STOP, then SAVE —
  even when there was nothing left to check. Now a SAVE button sits in the run controls the whole time a run is
  going, between STOP and DISCARD, and stops the run at the moment it is clicked before saving it in the same step.
  STOP is unchanged for anyone who wants to check the time or the loot first. Saving now also shows a clear "Saving
  this run…" indicator in the window itself, with every control disabled until it is done, so a slow save (a
  five-character group can take several seconds) is never mistaken for nothing happening.
- **RESUME now catches up what EVE wrote to the gamelog while the app was closed.** Before, resuming a run after a
  crash or a closed app (the RESUME button in UNFINISHED, or the startup notice offering the same choice) picked the
  clock back up but left a gap: any bounty, mining or enemies from the time nothing was watching the gamelog were
  simply gone, because the watcher only ever tails forward from where it started. Now, on that same RESUME, the
  character's gamelog is read once from the run's last confirmed-alive moment up to the resume — across a new
  gamelog file too, since EVE starts one per session — and applied to the resumed run exactly once, with a toast
  ("Caught up 3 bounty payouts, ... from while the app was closed") saying what it found. The live tail itself is
  never touched and the DPS graph never shows a historical peak; a run nobody resumes gets nothing read in.
- **Homefronts are now recognised as homefronts, with their own kind.** Before, running a Raid, a Metaliminal
  Meteoroid or any of CCP's other 22 homefront dungeons threw away which one it was — the run window discarded the
  match it already had, so a saved homefront read back as an ordinary combat or ore site. Now the run window, the
  detail screen and the runs overview all show "Homefront" plus its kind (Raid, Metaliminal Meteoroid, …), and a
  mining homefront (Metaliminal Meteoroid, Abyssal Artifact Recovery) gets its own MINING section the way a combat
  homefront already gets ENEMIES and BOUNTY. The run window itself now also shows MINING live on any run, of any
  kind, the moment someone starts mining on it — not only after SAVE. Never a guess: a copied site name matching more
  than one dungeon still stores no id, exactly as an unmatched site always has. Existing runs with an exact homefront
  name are repaired once at startup; a published run that gets corrected this way is marked for republishing rather
  than silently updated on the server.
- **The site catalogue now carries a site's own gameplay notes, and its ship restriction is complete.** The SDE's own
  guidance for a site — recommended fleet size, expected time, roles — is now imported and shown behind a link next
  to the site in the run window, the same way the allowed-ships list already is. The ship restriction itself used to
  read only ship groups, so every 5-person homefront showed its T1 cruisers as forbidden even though CCP allows them
  by individual hull; those hulls (and any other site's individually-listed ships) now show up correctly, and a
  ship excluded by name is never shown as allowed. Existing installs re-import once to pick this up.
- **The runs overview now pages by month instead of showing only the newest 50 activities.** Before, anything past
  the 50 most recent activities was simply invisible on screen, even though it was still in your database — a busy
  stretch could silently cut a day's own total in half. Now you see one month at a time, with previous/next buttons
  to browse, and a month total (ISK and activity count) above the days. RUNNING and UNFINISHED stay above the month
  regardless of which one you're viewing, and a fleet-filtered view or a server tab pages the same way.
- **Mining now shows up on the run, per character and per ore.** A dedicated mining run shows it live in the run
  window; any other activity — a combat, data, relic, gas or wormhole site — shows it on the saved activity's
  detail screen once it's clear there is something to show. Units mined, critical strikes and residue are all
  shown, with a total value alongside; before, mining only ever added to your character's lifetime total and left
  no trace on the run itself. This works for any mining — a homefront, an ore anomaly, a belt, or a plain mining
  fleet — not only for homefronts. Mutanite is valued at its fixed NPC buy price, shown as such; every other ore is
  priced the same way loot already is. Mining now also counts toward TOTAL ISK. Fixes a long-standing gamelog bug
  along the way: the "residue" line from an almost-depleted asteroid was being silently dropped instead of counted.
- **On a run you fly together with a fleet, you now see each other's loot live — who found what, and what it is
  worth — under LOOT in the run window.** Before, a fleetmate's loot never reached you during the run: loot sharing
  could not be switched on anywhere, and even when it went out it was only an ISK figure, never the items. Now every
  other pilot on the run gets their own block under your group's, with their items valued the same way yours are,
  updated as they copy their loot; it stays out of your own totals. On such a run your loot and bounty are shared
  unless you say otherwise: the top of the run window shows SHARING with a loot and a bounty button, and one click
  stops sharing that for this run — the others' screens drop it at once. Outside a shared fleet run nothing changes:
  loot now has its own checkbox under Settings → Privacy & Sharing and in each fleet's SHARING dialog, off by
  default like bounty. Everyone on the run, and the server, need this version for the items to arrive; with an older
  server or fleetmate the items simply don't travel and the ISK figures behave as before.
- **Mining now shares the same way loot and bounty do.** Before, mining only ever showed on the machine of the pilot
  who mined it, so an FC could never see what the fleet as a whole pulled out of a site. Now MINING in the run window
  adds a "fleet mined" line for whoever on the run shares it, and — on a Metaliminal Meteoroid homefront, whose
  5,000-unit asteroid is a known amount — a "remaining" line as well; an ordinary mining fleet just sees the total. A
  member mining shows as evidence for the run's attendance the same way bounty already does. Mining has its own
  checkbox under Settings → Privacy & Sharing and in each fleet's SHARING dialog, and its own button on the run
  window's SHARING strip, off by default like loot and bounty; the fleet's own total is a lower bound, said on
  screen, since a pilot not sharing or not on EVE Together is missing from it.
- **Fleet runs now publish themselves, and your fleetmates' runs come in by themselves.** Before, nothing reached the
  server until someone pressed PUBLISH, and nothing came back until you pressed it again — so whoever published first
  never saw the other pilot's run, name or loot without a second press. Now a run you fly in a fleet on a coupled
  server goes to that fleet's own server the moment you save it (and when the app saves it for you a day later), and
  again whenever you correct it afterwards; the server lets everyone else in that run know, and their app pulls it in
  straight away, so each of you sees the combined run with both names and both hauls within moments. The overview
  says where each activity stands — publishing…, published to &lt;server&gt;, queued for &lt;server&gt; while you're
  offline, or failed with a RETRY button — and anything still waiting goes as soon as the connection is back. Solo
  runs, and runs in a fleet that lives only on your computer, still wait for PUBLISH, and a run is never sent to a
  server other than its fleet's own. Settings → FLEET RUNS turns it off if you'd rather publish every run yourself.
  The server needs updating too for fleetmates' runs to arrive by themselves; with an older server your own runs
  still publish on save, and the others' arrive the next time you publish or reconnect.
- **Several of your own characters flying one activity together, without a fleet, now all count toward TOTAL ISK
  in the run window.** Only the character the window happened to be showing had their bounty counted live; the
  others' payouts were missing until you saved, when the detail screen already added them up correctly. The BOUNTY
  section in the run window now also lists each character's own share, the way the detail screen already does.
  Nothing changes for a run flown in an actual EVE fleet.
- **Abyssal runs now have their own CONSUMABLES section**, in the run window and on the saved detail screen, for
  the filament that opened the pocket. The filament type comes from the pocket's own tier and weather; how many you
  needed is filled in from your ship's hull class where that is known — for now only a destroyer, which needs two —
  and is always yours to correct, for instance if someone went in without one. Its cost is priced the same way
  loot is, and is subtracted from TOTAL ISK rather than shown apart from it. In a fleet, or flying several of your
  own characters together, each pilot's own count and cost are shown separately.
- **The runs overview's short line now names a fleet mate the same way the line under it does.** A pilot this
  machine never linked showed as "character 883434905" on the short line while the row underneath, once opened,
  already read their real name. Both now use the same name, in the same order: the name recorded on the run, then
  a locally known name, then the bare id only as a last resort. An abyssal run with no known tier or weather no
  longer reads "Abyssal Abyssal" on that same line.
- **A run left running when EVE Together closed or crashed can now be resumed.** Before, a run like that could only
  be saved as it stood or thrown away — there was no way back into it. A notice offers it back the moment the app is
  open again: "EVE Together closed while &lt;run&gt; was running", with Resume (picks the clock back up from its
  original start, with everything it already collected) or Keep stopped; it never takes the keyboard while you're
  looking at EVE. The same run also shows up in UNFINISHED with a RESUME button alongside SAVE and DELETE, for
  whenever you get to it. Its stop time is now honest, too — while a run is going, the app keeps a note of the last
  moment it saw it going, so a run left open overnight is recorded as ending close to when you actually closed the
  app, not the next time you opened it. An abyssal whose twenty-minute pocket has already collapsed offers no plain
  Resume, since there is nothing left to step back into; several of your own characters flying together resume as
  the group they were.
- **Tools → Start run now offers every type that can be started by hand, not just Site and Abyssal** — a mission is
  in the list too now, asking only for a typed name. Whichever type you picked last is remembered and pre-selected
  the next time you open the dialog.
- **Every screen now shows the same total ISK for a run.** The runs overview and its day total used to add up only
  bounty and loot, so a mission showed a few million less there than on its own detail screen — its ISK reward and
  bonus were left out. The overview, the day total, the detail screen, the run window, UNFINISHED and "ISK today" on
  the dashboard now all show one figure: everything the run earned, less what it lost. "ISK today" counts mission
  rewards and loot as well now, not only bounty. A bonus that expired before you stopped the run counts nowhere; one
  you made in time counts everywhere. A mission flown with several of your own characters counts its reward once, not
  once per character. An unfinished run that lost more than it made now shows below zero instead of "0 ISK". Runs you
  saved before this update are added up again once, the first time you start it.
- **BONUS ISK on a mission now lines up under ISK and LOYALTY POINTS instead of crowding its label**, the same bug
  TYPE had before, when it was only fixed for that one row. Fixed structurally this time, for every key/value row in
  the run window and the activity detail screen, whatever the value turns out to be (a figure beside a countdown
  chip, an icon beside a name) rather than plain text alone.
- **A mission run now records where you were when you started it, the same way a site does.** A regular agent's
  mission (no "Report to" line in the capture) used to leave LOCATION reading "not recorded" even though the run
  window showed your own system live for as long as it stayed open — it was simply never stored. The mission's own
  destination is now shown too, as its own MISSION LOCATION line, resolved from the capture's own Location line
  when the SDE recognises the name exactly. Neither the ACTIVITY header nor a run window's own title ever reads
  "· not recorded" any more, for any run type — when the location genuinely is not known, only the type shows,
  the same rule an abyssal already had.
- **A fleet abyssal pilot who stops and starts their own run again is now shown as "in" again within seconds.**
  Stopping and picking your own leg back up used to leave you reading "out" on everybody else's FLEET line until the
  twenty-minute cut-off quietly corrected it. The resume is now announced the moment it happens, in its own message
  so an older client or a server not yet updated simply ignores it rather than misreading it as something else. This
  needs the server to be updated; until then a resumed leg still corrects itself once the twenty minutes are up, as
  before.
- **Several of your own toons flying one abyssal together now each keep their own clock.** Before, picking more than
  one of your own characters at START (or adding one mid-run) started and stopped every one of them together with
  whichever toon this window was showing — a toon that jumped in later, or came out earlier, was timed as if it had
  done neither. Each toon now starts when it crosses into the pocket on its own account and stops when it comes back
  out on its own, exactly like a separate fleet member would; a toon that never goes in is never started at all. SAVE
  still commits the whole group together in one go, and the saved activity's span still runs from the first toon in
  to the last one out.
- **A fleet abyssal now times every pilot on their own way in and out.** A member's clock used to start the moment
  the fleet commander jumped and stop the moment he pressed STOP or left the pocket — even for a member still inside,
  who lost the rest of their time. Now each pilot's run starts when they themselves jump in and stops when they
  themselves come out, and the saved activity runs from the first pilot in to the last one out, with each pilot's
  own time still listed beside it. While the run is on, a FLEET line under the clock shows the fleet's own clock —
  when the first pilot went in, how long ago, and who is still inside — and turns amber when you are out and waiting
  on the others. Every pilot, not only the commander, now has START and STOP for their own run; ending the run for
  everybody stays the commander's.
- **The commander can set up a fleet abyssal before anyone goes in.** With the run window open in a fleet and the
  tier and weather picked, the members are offered the run straight away — "Fierce Dark · Osmon" — instead of only
  once the commander is already inside. A member who joins is armed and waiting: the window says plainly that the
  run starts by itself when you jump into the abyss, with START beside it for starting by hand, and it says so just
  as plainly when it cannot see you go in. Closing a prepared run before anyone went in takes the offer back off
  every member's screen and leaves nothing behind; a member who never went in is told when the run ended without
  them. The early offer needs the server to be updated; until then members are offered the run once the commander
  is in, as before.
- **An abyssal pocket no longer shows a BOUNTY section**, in the run window or the saved activity screen — there is
  no NPC bounty in one at all. A run flown in one is also no longer saved as "Unnamed site": it is now named after
  its filament, the same way EVE itself does — "Agitated Dark", say — in the run window, the runs overview, the
  saved activity screen and the server tab. A pocket's tier and weather are now actually saved with the run; before
  this they lived only in the open window and were lost the moment it closed.
- **A fleet member joining a shared run now sees the same site type as the commander** — a Data Site copied by the
  commander used to reach a member reading only the honest-but-poorer "Site", since the fleet's own start message
  never carried the scanner group behind it. The same message now also carries an abyssal's tier and weather, so a
  member's window shows the commander's real answer instead of falling back to whatever this member's own client
  last remembered from some unrelated abyssal — reported by Raymond as "T0 Dark" on a run Jithran had started as a
  T3 Dark. Changing the tier or weather mid-run now reaches every member already on the run, too. An unknown tier
  or weather now reads "not set" rather than guessing the first one on the list.
- Mission reward lines EVE Together cannot yet recognise are now kept untouched with the run, so they can be classified later instead of disappearing.
- Mission runs now show their agent's level instead of abyssal weather and tier badges.
- Mission runs now have their own MISSION section, in both the run window and the saved activity screen: the agent by name (a regular agent's mission, which states no agent at all, says so honestly), ISK, Loyalty Points, items and any reward line the app does not yet recognise. A bonus reward counts down while its time window is open, reads as expired once it has passed, and drops out of TOTAL ISK the moment it does — a bonus you actually earned before stopping the run stays counted even if the window sits open a while longer before you save it. A mission run can now also be marked blitzed, cherry-picked, cleared or full clear, the same as a site, and remembers your last choice for missions on their own.
- The saved activity screen now shows a mission's agent by name instead of a bare id.
- **An abyssal's saved activity screen no longer shows a MISSION section.** A pocket's own tier and weather were
  being counted as a reward, so the screen picked up a MISSION section with two rows reading the raw stored value
  ("ABYSSAL FILAMENT 3|Dark") — one per run of the group. LOCATION on an abyssal, and the ACTIVITY header beside
  it, no longer read "not recorded" either: both now show the system the filament was entered from when it is
  known ("entered from Dresi"), and the LOCATION row is left out entirely when it is not.
- **Copying an important mission now starts a run.** The warning sentence EVE shows above an important mission's own header — "This is an important mission, which will have significant impact on your faction standings." — used to sit in front of the name and stop the run from starting at all. Its item reward is now also read correctly, even written as "1 x …" rather than "1 × …", and gets its item type looked up the same way any other item reward does. MISSION now shows when a run came from an important mission.
- **MISSION no longer reads "nothing recorded" for a mission run window did not itself start.** Reopening the run window after restarting the app while a mission was still going, or opening a second window on the same run, used to lose the agent, the level and every reward line — they were only ever handed to a window at the moment it started the run, and adopting an already-running one carried none of it over, even though it was sitting in the store the whole time. The saved activity screen was never affected.

### Added
- **A run now shows what it actually was.** A Data Site, a Relic Site, a Gas Site, an Ore Site or a Wormhole used
  to be saved and shown as "Combat Site" regardless — now each reads under its own name, with its own icon, the
  same way in the run window, the saved activity screen and the runs overview. A mission run no longer reads
  "not known yet" either; it now says "Mission run" from the moment you start it. A run started by hand, or an
  older run saved before this, honestly reads "Site" rather than guessing what kind it was.
- **Keyboard shortcuts.** Common browser/desktop shortcuts now work throughout the app whenever it has focus:
  Ctrl+W closes the current tab or floating module window (through the exact same close question a running
  activity or an unsaved edit already asks — nothing is skipped), Ctrl+Tab/Ctrl+Shift+Tab and Ctrl+1…8/Ctrl+9
  switch tabs, Ctrl+Shift+T reopens the last tab you closed, F5/Ctrl+R refreshes the current screen, Ctrl+F
  jumps to its search box where it has one, Ctrl+, opens Settings, and Esc cancels a dialog. None of this ever
  fires while EVE itself has focus, none of it touches text-editing keys, and Ctrl+Alt combinations were left
  out entirely so they don't collide with AutoHotkey setups that already use them. A new **Keyboard shortcuts**
  section in Settings lists every shortcut, lets you record a different key, disable one, or reset it (or all
  of them) back to default — a clash with another shortcut is refused and explained rather than silently
  allowed. Only what you change is remembered, so a future update's new defaults still reach anything you
  never touched.
- The **UNFINISHED** band on the runs screen now shows each stopped-but-not-saved run's own **TOTAL ISK** so
  far, right beside its duration — bounty, priced loot and ISK-shaped rewards added up the same way a saved
  activity's or the run window's own TOTAL ISK is, so you can tell what you'd be keeping or throwing away
  before you decide. A run that truly earned nothing reads "0 ISK" outright; a run whose loot has no known
  price yet says so in words instead of guessing a number.
- **Bounty is now kept the moment it's earned, not only once you press SAVE.** A payout used to live only in
  memory until a run was saved, so a crash, closing the app, a run's window disappearing, or the day-later
  auto-save of a run you never got back to all threw its bounty away for good — loot never had this problem,
  and now bounty doesn't either. The UNFINISHED band's TOTAL ISK picks this up automatically. Bounty already
  lost this way before this fix cannot be recovered.
- **An activity can now be deleted, from an understated button at the bottom of its detail screen.** It
  asks you to confirm first, naming the site, how many of your own runs go and how much ISK — and, if it was
  already published to a server, says that deleting it here only removes your own local copy, never
  the server's. Deleting a multi-character activity removes every character's run that is yours; a
  fellow pilot's own run, pulled in from a server and shown read-only, is left in place, and the
  activity keeps showing whatever of it is still there. The runs overview updates immediately, and the
  screen offers to undo the delete right after.
- **Copying a combat site or mission while several of your characters are flying now starts the "whose
  run is this?" question with the character who actually copied it already ticked.** You can still tick
  on other toons, clear the tick, or leave it as is — nothing starts until you confirm.
- **Saved activities now keep every enemy type your gamelog saw**, even when you did not type a count. Those rows
  read “not counted” instead of a misleading zero, while the activity still shows how many enemy types it saw.
- **Starting a run now lets you register it for more than one of your own characters at once.** If
  several of your EVE clients are open, the "whose run is this?" question at START now lets you tick
  more than one — useful when multiboxing the same site on several toons. Each ticked character gets
  its own run, sharing one group. The activity window's character column now switches between the
  group's own runs — click a toon to bring up their loot, bounty and everything else, so you can
  register loot against the right character instead of only ever the one you started with. The header
  chip does the other half: it shows every character the run covers, and clicking it lets you change
  who a not-yet-started run is filed under, or bring another flying character into one that is already
  going. STOP and SAVE apply to the whole group of characters together, so one click ends the run — and
  saves everyone's own bounty, not just the character you started it on — for all of them at once.
  Saving a multi-character run is also markedly faster than it was, and the SAVE button now shows when
  it is working instead of appearing to do nothing.
- **Starting a run by hand — the START button on a character card, or Tools → Start run — can now
  register it for more than one of your own characters too**, the same way copying a site into the
  clipboard already could. Pick who it's for before pressing START (or PREPARE RUN for an abyssal
  standing by); starting from a character's own card still starts with that character ticked, and you
  can add others alongside it. Every character offered here still needs to be one of your registered
  characters — every one of them, whether or not its client happens to be running right now, same as
  before. Each ticked character gets its own run, sharing one group, exactly like every other way of
  starting a run for several toons at once. Reopening the "who is this for?" picker no longer clears
  everyone you had already ticked back to just the one it started with — it now remembers your picks
  for as long as the dialog stays open.
- Reopening the activity window, or a clipboard copy naming a running site, no longer gets confused
  when more than one of your characters has a run going at the same time — each is tracked and shown
  on its own. A character's own bounty meter (shown live on the FLEET panel, and saved with the run)
  now starts fresh with each new run instead of carrying over an earlier one's payout.
- A saved activity's **LOCATION and FIT are no longer lost** — both were visible on the run window
  while it was going, and now actually make it into the saved record instead of reading "not recorded"
  and "not recognised" afterwards.
- A saved activity's **BOUNTY** now shows who brought in what, one line per character, with a clearly
  set-apart total underneath — instead of one combined figure with no breakdown. The activity detail
  screen also shows a prominent **total ISK** figure right beside the duration, adding up bounty,
  priced loot and any ISK-form reward the run earned.
- A saved activity's **ENEMIES** now also breaks down per character, with a group total underneath —
  each toon keeps its own kill count from its own gamelog, and a hand-typed count survives switching
  the run window's character column instead of being silently lost.
- **The run window now shows a running total ISK beside the elapsed-time clock while a run is still
  going**, the same figure the saved activity's total ISK shows once it's over. For a run covering
  several of your own characters, this is the whole group's total, not just whichever character the
  window's character column happens to be showing — and it does not change value when you switch it.
- **Loot copied while running a site on several of your own characters now lands on the character who
  actually copied it**, instead of all being filed under a single run. A saved activity's **LOOT** now
  breaks down per character, with a group total underneath, the same shape BOUNTY and ENEMIES already
  had — and the running total ISK beside the elapsed-time clock now counts everyone's loot in, not just
  whichever character the window's character column happens to be showing. On the rare copy the app
  cannot tell whose client it came from, and more than one of your characters has a run going at once,
  it now asks whose loot it is instead of guessing or filing it under the wrong run.
- **The LOOT section is now grouped by character, on both the run window and a saved activity.** Each
  character gets a header with their portrait, name and what their loot came to, then the items it is
  made of — one line per kind of item however many copies it came in (three Metal Scraps read as one
  line of three), most valuable first, with the one item carrying most of the haul marked out. A copy
  you left out stays listed on its own struck-through line underneath, so it is never hidden inside a
  line that still counts. Folded away until you open it is every copy behind that subtotal, with a
  switch to count or leave out each one. The group's total sits underneath everything. An activity
  saved before loot was filed per character still reads fine: its loot shows under the character whose
  run it was recorded on, with nothing shared out afterwards that was never recorded that way.
- **You can now correct the loot of an activity you already saved.** On the saved activity's screen,
  leave out a copy that should not have counted, count one again, or rewrite a character's whole loot
  list by hand — the same corrections the run window always offered, now available after the fact for
  when you only spot the mistake once the totals are in. Every figure made of it moves at once: the
  character's subtotal, the group total, the total ISK at the top and the day's total on the runs
  screen. Only your own characters' runs can be corrected; a fleetmate's run that came in from a
  server is shown read-only. **Rewrite loot by hand** is now a clear button right beside each
  character's name, instead of a small link that was easy to miss.
- **Correcting an activity you already published says so, and never changes the server's copy on its
  own.** The saved activity's screen and its row on the runs screen both tell you the server still has
  the old figures, and the screen offers **Publish again** right there — nothing is sent until you
  publish it yourself, not even when you publish a different activity later.
- **A crash that used to vanish without a trace is now written to the log the moment it happens.** A
  fault on a background thread, or a task nobody was still awaiting, escaped every other net EVE
  Together already had and left nothing behind — there was no way to tell what went wrong, or that
  anything had gone wrong at all. Such a fault is now caught at the last possible point and written to
  the same `app-errors.jsonl` the app already keeps, with the exception's type, message and stack trace
  — never clipboard content or other player data. A line is also written on an ordinary, clean
  shutdown, so a crash can be told apart from the app simply being closed.
- **The runs overview's currently-running section now shows a proper card per character, not a bare
  line of text.** Each card carries the character's portrait, name and state, a live elapsed-time
  clock, and a START or OPEN button, laid out in the same fill-width card grid Fleets and Fits already
  use — so several people running at once are shown side by side rather than stacked as text, and the
  grid reflows to fewer columns as the window narrows.
- **An abyssal run now recognises which room you're in.** Room detection reads the same enemy
  observations the abyssal countdown already watches, so the app can tell rooms apart within one run
  instead of treating the whole pocket as a single undivided space.
- A fleet member's assigned fit now shows its **max velocity, warp speed and align time**, instead of
  one figure on the row and the rest hidden behind a tooltip — all three sit on the row where you can
  see them at a glance.
- **A site escalation can now be registered and recognised.** Typing an escalation's name into the
  activity window registers it against the SDE's site catalogue, and two escalations that share a name
  are told apart in the picker instead of looking like duplicates of each other.
- Days in the runs overview can now be **collapsed**, and a collapsed day keeps its summary visible —
  so a long history no longer forces you to scroll past everything you've already looked at.
- A fleet's HUD now has a **RepIn** metric, showing the incoming reps a member receives, alongside the
  existing outgoing figures.
- **A finished fleet's row now carries a RUNS button, and the stop dialog says how many runs the fleet
  completed.** A fleet's runs are now found through the group code recorded against it, so a fleet
  that has already stood down is no longer the one place its own history disappears from.
- **A standing-by fleet can now be signed off without leaving the roster.** Signing off used to mean
  leaving, which threw away your place on a fleet you only meant to step back from for a moment.
- **Loot can now also be captured as the difference between two cargo-hold snapshots, alongside the
  existing clipboard method.** Take a snapshot before you loot and another after, and EVE Together
  works out what changed — useful wherever copying the cargo window to the clipboard isn't practical.
- **An abyssal run can be registered by hand again.** There is no site and no running clock until you
  actually go in, and coming back out stops it, so starting one no longer depends on a signature you
  happened to copy first.
- **The loot strategy is now stored on the run itself, so a site you cherry-picked stays
  distinguishable from one you cleared out completely**, instead of that distinction being lost the
  moment the run was saved.
- **The runs screen now has a tab per source — Local, plus one for each server you're coupled to — and
  an activity can be published to one.** An activity is shown under the server it was published to,
  the same tab layout the fit browser already uses; with no server coupled there is no strip of tabs
  at all.
- **A run you stopped but never finished now shows up in its own band on the runs screen and can be
  finished from there**, instead of disappearing the moment its window closed with no record of it
  left anywhere in the app.
- **A fleet can now be stopped instead of only being finished.** Stopping puts a fleet back to standing by with its
  roster, its doctrine and its name intact, so the op you fly every Wednesday is started again next week rather than
  recreated from scratch. Its members are released the moment it stops and can fly in another fleet straight away.
  Finishing a fleet — **CONCLUDE** — still does exactly what it did and is still one way only; it has moved off the
  roster header into the window that **STOP** opens, where the three ways out of a running fleet stand side by side
  with what each of them costs. Deleting a fleet stays where it was, on the fleet overview: it is not the same size
  of decision and no longer sits next to the one you take every week. The window also names any run of yours that is
  still going and says plainly that stopping does not throw it away.
- **A fleet that has run out of people now stands itself down.** An op nobody is flying any more goes back to standing
  by on its own, so a fleet left running overnight is not still "active" in the morning, blocking the pilots on it from
  joining anything else. Two things bring it about: everybody has left the roster, or everybody's client has gone
  quiet. It happens for a fleet on a server even when the FC has closed their own client, because the server is
  watching; a fleet that lives only in your client settles up the next time you open the app. Either way it is a
  **stop**, never a conclude — the roster, the doctrine and the name stay exactly where they were and the fleet is
  started again with one press.
  The two are not treated alike, because one of them lies during downtime. "Everyone left" is taken at face value at
  any hour: whoever left stayed gone. "Everyone is offline" is held back through Tranquility's daily 11:00 UTC window,
  through any stretch where EVE is unreachable, for a couple of minutes after either of those, and for a couple of
  minutes after the server itself restarts — the moments when the whole fleet looks absent and none of them has
  actually gone anywhere. A fleet nobody has published into yet is never read as emptied either, so starting one
  before your pilots log in is safe. One pilot still flying keeps a fleet running however quiet the rest are.
  An automatic stop is credited to the fleet's owner and to nobody else — no member's client can stand a fleet down —
  and it says in so many words that it was automatic and which of the two reasons applied, both in the mail the roster
  receives and in the server's log. The owner is written to as well, which a stop they pressed themselves does not do.
- **There is now a RUNS screen, and it is the first place in the app where a finished run comes back.** Until now a
  run left the screen the moment its window closed and there was nowhere to see what you flew yesterday. RUNS in the
  rail opens the list: one row per activity — one site, flown once — however many of your pilots were on it, so six
  toons on one site are one row that names all six and unfolds into six, not six rows that look like six evenings.
  Above the list stands a lane per character with its own clock, and a pilot who is flying nothing keeps their lane
  and their START, because a toon that quietly drops off the screen is a toon you forget. Each row carries the time,
  the site, the crew, what you ran into, how long it took and what it paid — the payouts as separate chips beside
  each other and never added into one figure, since loyalty points and Evermarks have no ISK price to convert
  against. A reward form the screen has never seen still gets a chip of its own rather than disappearing. An
  activity with no loot capture says so instead of showing a "0", which would read as a valuation that came out at
  nothing. The rows sit under a day band with that day's total, and clicking one opens the activity in full.
- **A saved activity can now be opened in full.** One screen per activity, folded into the same sections the run
  window uses, showing the runs behind it, what you fought, what dropped and what it paid out. Which sections are
  there depends on what you flew: a mission names its agent and its level and lists its rewards one form at a time,
  a combat site shows enemies, bounty and loot. A section that is missing says why it is missing, in as many words —
  a mission has no rats to collect a bounty from, and a site pays in what it drops rather than in a reward agreed
  beforehand. A section that is there but empty says what was not measured, and never shows a "0": nothing was
  captured is a different thing from nothing was worth anything. Loot is valued per item type from EVE Together's
  own cached prices and never from the ISK column you copied, rows the price cache does not know are counted
  separately rather than treated as worthless, and a capture you excluded stays on screen, marked, counting towards
  nothing. A run whose start or end you corrected by hand says so beside its duration, because the corrected times
  replace the measured ones and the figure alone can no longer tell you which it is.
- **The server control panel can now take a backup and put one back.** One encrypted file holds everything a server
  needs to be rebuilt somewhere else: the whole database, the key that decrypts the stored ESI refresh tokens, and
  the TLS certificate your clients pinned. Restoring it on a fresh install brings the server back with every linked
  character still connected. The archive is an ordinary **AES-256-encrypted ZIP**: open it with 7-Zip or WinRAR and
  you can see exactly what is in your own backup, without EVE Together. Windows Explorer cannot open it — Explorer
  only supports the old, broken ZipCrypto. Because the ZIP format fixes its key derivation at 1000 rounds, the
  strength of an archive is the strength of its password: the panel asks for at least 20 characters and offers to
  generate one, shown once. That password cannot be recovered. Your configuration — the ESI client id and secret,
  the admin password, the database
  connection string — deliberately stays out of the archive, and the panel says so. Restoring is destructive, asks
  you to confirm in so many words, keeps an archive of what it replaced, and restarts the server afterwards.
- Characters whose location EVE Together may not read now produce **one** message naming them all, instead of one
  message per character. The wording says what is actually wrong — that their location cannot be read — rather than
  naming the abyssal countdown, which was only the first feature to need it.
- The character dialog's **ESI SCOPES** block now tells you what that character shares right now: hover it for the
  list, read from what EVE actually granted rather than from what the app asks for. A character that granted nothing
  says so.
- Notification cards are a little wider, so the last button keeps the same margin from the edge as the text does.
  Three buttons used to fill the card exactly, which left the rightmost one sitting against the border.
- Notifications no longer sit on top of the title bar or the status bar. They keep clear of whichever of the two
  is actually on screen, so a card counted from the top or the bottom lands inside the window rather than over it.
- **Clipboard watching now works on Wayland for anything you copy.** A fit copied from a browser produced nothing
  before: the change was noticed, but the text could not be read, because the reading went a different way than the
  notification did. Both now go the same way.
- A copied fit is offered again if you copy it again. Previously a fit was offered only once per run of the app, so
  a question pushed aside by a newer fit could never come back.
- Importing a fit you copied now opens the **From EFT / DNA text** window with the fit already pasted in, instead
  of putting it straight into your library. You see what is about to be added, and can correct it, before it lands.
- **Ignore** on a copied fit closes just that card without silencing the fit. Copying the same fit again after
  answers the question again, instead of it going unaskable.
- A notification that asks you something now stays on screen until you answer it. The offer to import a fit you
  copied used to disappear by itself after about five seconds, so the question was often gone before you looked at
  it. Every notification with buttons keeps this behaviour, and each one now carries a small cross so you can put it
  away without answering.
- The clipboard reading in the status bar at the bottom of the window is now clickable and takes you straight to
  **Settings → Privacy & Sharing**, where the switch, the explanation of what is read, and — if your desktop cannot
  report a change — the reason it is unavailable all live.
- Copying a cosmic signature or anomaly out of the scan window now shows a toast with what the site catalogue knows
  about it — archetype, faction, DED rating and any ship restriction, only for whichever of those are actually on
  record. A site whose name matches more than one catalogue entry shows what they agree on plus how many variants
  there are, rather than guessing which one you scanned. This does not start anything yet.
- EVE Together can now watch your clipboard and recognise two things you copy out of the game: a
  fit in EFT format, and an inventory listing. **It is off until you switch it on** in
  **Settings → Privacy & Sharing**, and the status bar at the bottom of the window says which it
  is — `CLIPBOARD OFF`, `CLIPBOARD WATCHING` or `CLIPBOARD UNSUPPORTED` —
  from the moment the app starts, so you never have to open settings to find out. While it is on,
  your desktop tells the app whenever you copy something; it reads the text, looks at its shape, and
  passes a fit or an inventory to the features that asked for it. Everything else — a password, a
  link, part of a conversation — is dropped the moment it is read: not stored, not buffered, never
  written to the log window or a log file, never attached to an error report, and never sent
  anywhere. The settings screen lists which features are listening, read from what is actually
  registered rather than from a written-out list, so today it tells you plainly that nothing is —
  and while nothing is listening, the clipboard is not read at all, so switching this on before
  there is a feature that uses it changes nothing.
  This works on Windows and on Linux under Wayland (through `wl-clipboard`, which needs to be
  installed). It is not supported on macOS, on a Linux session running plain X11, or on a Wayland
  desktop that does not offer clipboard notifications — GNOME is the one to know about. Those say
  `CLIPBOARD UNSUPPORTED` rather than quietly polling, because spotting a change by polling means
  remembering what was on the clipboard, which is the one thing this feature promises not to do.
- The fleet metrics window can now be switched between three densities with the **LIST / GRID /
  COMPACT** buttons above the member list, so the screen stays readable as a fleet grows past ten
  or thirty members. **List** is unchanged and remains the default: every figure plus the live
  graph. **Grid** puts members in cards side by side, carrying every live figure (DPS out/in, cap,
  neut) one size down with the graph below them. **Compact** gives each member a single line with
  every live figure and no graph. Both denser views leave out only the session bounty; the line
  under the header always names what the current density leaves out. The chosen density is
  remembered for the next session (one setting for the whole install, not per fleet).
- Fleet members can now be dragged into the order you want them in, in all three densities. The
  order is remembered per fleet on this machine — it survives a roster refresh and a restart, is
  the same whichever density you switch to, and is never sent anywhere. Members the saved order
  does not know join at the back, and a saved member who has since left the fleet is simply
  ignored.
- A fleet member's solar system now reads green when they are standing in the fleet commander's
  system, in all three densities — the same green the header's `WITH FC` badge turns at full
  presence, off the same count, so you can see who is with the FC without reading and comparing
  system names. Members elsewhere keep the neutral colour, and when there is no fleet commander or
  the commander shares no location every location stays neutral rather than showing half a signal.
- Fits can now be imported from an EVE Workbench link. Paste a fit URL (or just its fit id) into
  **Fits → Import → From fit link…** and the published fit is fetched from EVE Workbench's public
  API and stored in your Local library, exactly like a pasted EFT block — including duplicate
  detection. Tech III cruisers keep their subsystems. A link that is not an EVE Workbench fit, a
  fit that does not exist or is not published, and an unreachable or slow EVE Workbench all report
  what went wrong instead of failing silently. Nothing is sent to EVE Workbench beyond the fit id
  you pasted, and the rest of the app keeps working when the site is unavailable.
- The release pipeline now builds and publishes the server as a Docker image to
  `ghcr.io/evetogether/eve-together-server` on each GitHub Release — tagged `:latest` (always the
  newest build) and with the release version (`:X.Y.Z`) — so self-hosters can pull the image that
  `docker-compose.yml` already references instead of building it from source.

### Changed
- **The run window and the saved activity screen are now built from the sections each kind of activity
  needs.** Nothing looks or works differently yet: combat, data and relic sites, abyssal runs, missions,
  group runs of your own characters and fleet runs all show the same sections as before. What this changes is
  what comes next: missions, mining and homefronts can each get a section of their own without the other
  kinds of activity changing along with them.
- **The runs overview now opens with only the most recent day expanded**, all earlier days collapsed
  but still showing their summary — it used to open with every day expanded, which meant scrolling
  past your entire history to see today. This applies per tab, so Local and every coupled server keep
  their own most recent day open.
- Copying a clean loot capture — nothing worth noting in it — no longer raises a toast. Only a capture
  with something to say about it interrupts you now.
- **An escalation's destination system now resolves instantly from EVE Together's own SDE, with its
  jump count filled in from ESI only once the activity window is actually open**, instead of the whole
  thing waiting on an ESI round-trip before it could show anything at all.
- **The character picker is redesigned with hexagonal portraits and a visible selection mark**,
  matching the character portraits used elsewhere, and grows to fit its content instead of being
  cropped. A character you can't select shows a plain-English reason instead of just sitting there
  disabled, and the fleet invite window picks up the same portraits.
- The ESI-invite hint text is clearer about what it's telling you, and its checkbox no longer sits
  there disabled and inert for a state that never did anything.
- **When two things try to start the same run at once, the fleet commander is asked and the member
  switches over themselves**, instead of one silently overwriting the other.
- The LOOT section has been restyled, and its in/out toggle button is now the text itself rather than
  a separate button beside it.
- **The fleet overview has been reworked: fleets are grouped, and a finished fleet is shown for the
  first time** instead of disappearing the moment it's concluded — with a Delete action in its place,
  since there's nothing left to disband. The member list and the running-fleet band both scale with
  the window's width, from a full card down to a single-line row, and your own active fleet now sorts
  first. A roster past a handful of members collapses behind a "show all N", keeping only the fleet
  commander, you, and anyone needing attention visible by default; a member who shares nothing with
  you now says why instead of looking simply offline. Per-row actions — Join, Request, Leave, Manage
  and the rest — sit directly on the row when there's room, and move into an overflow menu when there
  isn't, never both and never neither.
- **A wormhole is no longer mistaken for a site, and copying a signature over a run that's already
  going now asks first** instead of quietly replacing it.
- **What kind of activity a run is is now carried from where it started, instead of being worked out from "is this
  an abyssal run".** Everything that was not an abyssal run was filed as a combat site, so a mission was stored as a
  site and came back as one — and there was no way to record any other kind at all. Each kind now stands on its own
  footing, which is what lets homefronts, missions, relic and data sites follow. Two things you can see straight
  away: a mission is no longer asked which loot strategy it was flown on — "full clear" is a site's word and means
  nothing on a mission — and the setting for reading loot off your clipboard is now called **Run loot** rather than
  **Abyssal run loot**, because it never was about the abyss. It works exactly as it did.
- **Starting a run by hand now takes you to the run.** START used to create the run and leave you looking at a
  sentence: no clock, no loot, no STOP, and no way to the screen that has them. It now opens the activity window on
  that run, the same window a copied signature lands in, and the dialog closes behind it. **Start Run** is a proper
  dialog too — it is as tall as the three things it asks for, where it used to be a window three times the height of
  its own content with an empty black field below. Its one Dutch button is gone: **ACHTERAF INVOEREN** now reads
  **BACKDATE**, with the date and time it governs directly under it.
- **Copying a scanned combat site now starts the run straight away, instead of offering a card you had to come back
  to the app to click.** The run window comes up on that site with the clock already going, behind whatever you are
  looking at and without taking the keyboard — so you can stay in EVE and fly. If more than one of your characters
  has an EVE client up it asks which of them this run is for, exactly as it already did elsewhere; with one
  character it simply starts. This is only for a copy holding one fully scanned combat site: several sites at once,
  a half-scanned one, or anything that is not a combat site still shows the card it always did. A run that belongs
  to a fleet is untouched — that one still waits for the commander, and the copied site waits behind it.
- **A run the fleet commander starts now arrives as a notification you accept, rather than a window that opens by
  itself.** The card names the site and the system, so a pilot flying something else entirely can see at a glance
  that this is not their run, and it stays on screen until you answer it — nothing takes it away, because a card
  that withdraws itself is a card you can miss, and missing it means missing the group your runs are recorded
  under. Accepting it opens the run window and files your run under the commander's group; leaving it alone does
  nothing at all, and starts no run. If more than one of your characters has an EVE client up, it first asks which
  of them is flying this one, offering only the characters actually logged in. Were you already in a run, that run
  is the one that joins the group. Accepting an offer for a run the commander has meanwhile ended opens nothing and
  says why. If you would rather have the window straight away, as before, there is a switch for it under
  **Settings → Interface → FLEET RUNS**.
- **The self-hosted server no longer keeps its identity in the build output.** Its data directory — database, TLS
  certificate, token-protector key — used to sit inside `bin/`, where a rebuild, a `dotnet clean` or a fresh clone
  silently took it away and every paired character was lost for good. It now defaults to the per-user data folder
  (`%LOCALAPPDATA%\EveUtils.Server`, `~/.local/share/EveUtils.Server`), and can be set with `Server:DataDirectory`
  next to the existing `EVEUTILS_SERVER_DATA_DIR`. A bare-metal server started on the default moves an older
  installation's data across on first start. Docker installs already used `/data` and are unaffected.
- **The server refuses to start on an identity it just invented.** If it had to generate a new token-protector key
  while characters are still paired, their stored refresh tokens can no longer be decrypted — so it stops and says
  so instead of coming up and losing them quietly. Restore the key that belongs with the database, or start once
  with `--accept-new-identity` to accept the new identity and pair everyone again. A regenerated TLS certificate
  does not stop the server; it is logged, and clients re-pair.
- The two buttons at the end of a coupled-server row — the gear and **DECOUPLE** — are the same height now.
  Each used to be as tall as whatever it contained, an icon against smaller text, so the pair never quite lined up.
  The fighter bay's reserve rows are the same pairing and follow the same rule.
- Dependency maintenance (Dependabot): Avalonia and its companion packages
  (`Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Controls.DataGrid`, `Avalonia.Fonts.Inter`,
  `Avalonia.Headless.XUnit`) to 12.1.0; `Microsoft.AspNetCore.OpenApi` to 10.0.10,
  `Scalar.AspNetCore` to 2.16.15, `Microsoft.AspNetCore.SignalR.Client` to 10.0.10,
  `Npgsql.EntityFrameworkCore.PostgreSQL` to 10.0.3; and the release-workflow actions
  `actions/setup-dotnet` (v6), `docker/login-action` (v4) and `docker/build-push-action` (v7).
  `Microsoft.OpenApi` is held at 2.7.5 — the 3.x major is incompatible with the
  `Microsoft.AspNetCore.OpenApi` 10.0.x source generator.
- Dependency maintenance (Dependabot): Avalonia and its companion packages
  (`Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Headless.XUnit`)
  to 12.1.1, kept in lockstep in one batch; `Avalonia.Controls.DataGrid` to 12.1.2 (its own release
  line runs ahead of the rest of the Avalonia packages).
- Dependency maintenance (Dependabot): `Microsoft.Data.Sqlite`, `Microsoft.AspNetCore.SignalR.Client`
  and `Microsoft.AspNetCore.OpenApi` to 10.0.11; `Grpc.Net.Client` and `Grpc.AspNetCore` to 2.83.0
  (2.81/2.82 were never published, so this is a direct step with no skipped versions).
- Dependency maintenance (rest of what `dotnet list package --outdated` flagged, beyond
  Dependabot's open-PR cap): `Microsoft.NET.Test.Sdk` to 18.9.0, `AvaloniaUI.DiagnosticsSupport`
  to 2.2.3, `CommunityToolkit.Mvvm` to 8.4.2, `Scalar.AspNetCore` to 2.17.1,
  `SQLitePCLRaw.bundle_e_sqlite3` to 3.0.5, the `Microsoft.EntityFrameworkCore*` family and
  `Microsoft.Extensions.DependencyInjection.Abstractions`/`Hosting.Abstractions`/`Http` to
  10.0.11, `Microting.EntityFrameworkCore.MySql` to 10.0.10,
  `Microsoft.IdentityModel.Protocols.OpenIdConnect`/`System.IdentityModel.Tokens.Jwt` to 8.22.0,
  and `Google.Protobuf` to 3.36.0. `xunit.v3`/`xunit.runner.visualstudio` 4.0.0 (major) held back
  pending review.

### Fixed
- **The runs screen, the home dashboard and an open activity now keep up with everything that happens to a
  run, without being reopened.** Until now each of them only followed a handful of actions, and every other
  change sat unseen until you opened the screen again: a run stopped from its own window never showed up in
  UNFINISHED, a run saved from its window stayed in UNFINISHED as well, a payout landing after STOP didn't
  move that run's TOTAL ISK, publishing to a server didn't say "queued", an activity a server synced in
  didn't appear at all, deleting or restoring a saved activity didn't move **ISK today**, and an open
  activity's detail screen never changed unless you changed it right there. All of that now shows up on its
  own — a detail screen whose activity is deleted somewhere else says so and offers Undo, and one that is
  restored comes back. It stays calm while you fly: a burst of payouts is picked up a few times a second,
  not once per line, and nothing jumps while the screen catches up — an evening you folded stays folded,
  an activity you opened stays open, the list stays scrolled where you left it, and a loot list you are
  writing out by hand waits until you save or cancel it.
- **DISCARD on your own run now actually throws it away.** The runs overview used to keep counting up
  a discarded run's timer until the screen was reopened, and the run itself still turned up in
  UNFINISHED afterwards even though you'd already thrown it away — both are fixed. A toast right after
  offers **Undo**, in case that was a misclick on a run worth keeping — Undo now also puts the run back
  in UNFINISHED right away instead of only after reopening the screen. Ending a shared run as fleet
  commander is unchanged: it still leaves every other member's own run exactly as it was, saved or not.
- **A saved activity now remembers each participant's name, instead of only guessing it from whoever
  happens to still be logged in.** A pilot's name is written down the moment their run starts, so it
  keeps showing correctly on a saved activity even after that character has logged out or been removed
  from the machine — and the same goes for a fellow pilot's run pulled in from a server. The note
  explaining that names weren't recorded yet is gone from any activity where they now are; an activity
  saved before this fix still reads exactly as it did.
- **A saved activity's LOCATION, and the ACTIVITY panel's own heading, now show the solar system's
  name instead of its bare id** ("system 30004097"). Old activities with a recorded system now show a
  name immediately too.
- **Large ISK, Loyalty Points and prices copied with space-separated digits now keep their full value.**
  EVE's normal, non-breaking and narrow non-breaking spaces are all read as the same thousands separator.
- **Mission rewards copied from EVE no longer disappear from a run when their clipboard line has trailing
  whitespace.**
- **Missions without a Report to line now keep the name from their Objectives heading**, instead of beginning as an
  unnamed site.
- **The character picker now always opens on top of the EVE client, and remembers where you last moved
  it**, reopening in the same spot instead of re-centering every time — falling back to centered only
  if that spot is no longer on a connected monitor.
- **The runs overview's running-runs cards no longer go blank and claim "nothing running" while a run
  is actively logging.** A leftover run stuck in a running state — after an earlier crash, say — or
  two people running at once, used to make the whole display give up; now each character's card is
  looked up on its own, and the cards update the instant a run starts or stops instead of only when
  the screen is reopened.
- **An expanded day in the runs overview is readable again.** Its heading fell through to the Fluent
  theme's default accent-blue highlight, which put dim, barely-visible text on a bright blue
  background; it now keeps EVE Together's own colours.
- **Today's ISK total on the dashboard now counts every saved run, not only the ones still being
  tracked live.** A run you'd already saved and closed used to drop out of the day's total.
- A clipboard copy is now attributed to whichever of your characters actually has the EVE window in
  focus, so multiboxing several clients no longer risks crediting a copy to the wrong one.
- The runs overview now refreshes live the moment a run is saved anywhere else in the app, instead of
  only showing it after the screen is reopened.
- The fleet edit dialog is now sized to its content, so its date pickers can no longer overlap its
  action buttons.
- **The activity window's list of who was on a run, and the ISK payout split between them, actually
  work now.** Both were silently reading from a list that was never filled in, so every run looked
  like it had nobody on it and the payout split divided across nobody at all.
- **Copying an ambiguous scan no longer risks opening a run for the wrong character, or none at all.**
  It's asked with the same candidate list RUN START already uses, before anything opens.
- **Loot capture now prefers the run belonging to the activity window you actually have open**, instead
  of guessing at a store-wide "current run" that could point at a different one entirely.
- A run that stays solo because your pilot is in a fleet that hasn't started yet now says so, instead
  of quietly going solo with no explanation.
- **The solo notice no longer guesses which of your fleets a run belongs to.** When more than one of
  your fleets is still forming, it used to name whichever one happened to come back first — possibly
  the wrong one. It now says how many fleets are forming and names none of them, and still names the
  fleet when there's only one.
- The notice that a fleet hasn't started yet no longer holds up its command controls — the two now
  refresh independently, so the controls are usable the moment they're ready.
- **A server that keeps refusing to refresh a token is no longer hammered with retries.** Failures are
  now recorded, and a token EVE itself has revoked stops being retried at all, while the backoff for
  an ordinary transient ESI fault continues as before.
- ADD TOON's character picker no longer offers a character who is already in the fleet.
- A click in the multi-select character picker now adds or removes just that row, instead of replacing
  your whole selection with it.
- The under-strength warning no longer falls below the fold — it sits in front of the dialog again,
  where you actually see it.
- Every fleet screen now has a way out, and three tabs open on the same fleet can be told apart instead
  of looking identical in the taskbar.
- **The LOOT section shows your run's loot again, instead of saying it cannot tell which run you mean.** It used to
  ask the app which run was going rather than reading the run the window was already on, so every run you had stopped
  without saving or discarding it went on competing for the answer. With eleven of those left standing, the section
  said "11 runs are running, so which one's loot to show is ambiguous" and showed nothing at all — for every run from
  then on. Those runs are harmless now and nothing has to be tidied up for it.
- **A run left going when the app closes is ended the next time it starts, instead of being handed to the next
  window.** A run outlives its window on purpose, but it was also outliving the whole app: opening EVE Together could
  present you with a run that had started the day before, its clock reading over twenty-four hours. Such a run is now
  brought to rest at startup — not saved and not thrown away, because what becomes of it is yours to say.
- **Your own run stays yours, even while you are in someone else's fleet.** A solo run of your own could be taken
  over by a window that then told you only the fleet commander was allowed to stop or discard it, leaving you no way
  out of it at all. Whether a run is shared has always been a property of the run itself, not of whichever fleet you
  happen to be in at the time.
- **With two clients open, EVE Together now asks whose run it is before the run window appears.** Copying a combat
  site used to open an empty window first — no character, no fit, nothing started — with the question arriving beside
  it a moment later. The question comes first now, and the window opens on the answer. With one client open nothing
  is asked, exactly as before. Dismissing the question no longer costs you the scan: the window still comes up on the
  site you copied, and **START** asks again.
- **A fleet you set up for later no longer costs your run its fleet.** A fleet you create but never start is a plan,
  not an operation — but a local one was counted as though you were flying it. Two of those standing by was enough
  for the app to be unable to say which fleet a run belonged to, so the run was filed under none of them and shared
  with nobody, without a word about it. A fleet now only counts once its commander has started it, whether it lives
  on a server or only on your own machine. And should you genuinely be in more than one started fleet at once, the
  run window says so and says what it means, instead of quietly going solo.
- **A member row in the compact fleet-metrics view can be grabbed anywhere on it, not just on its text.** The row
  showed the move cursor across its full width but only answered on the words themselves: pressing, dragging or
  right-clicking the gaps between the figures did nothing at all, which on a wide window was most of the row. Both
  reordering by dragging and the right-click member menu now work from any point on the row. It looks exactly as it
  did — a compact row is still a single line with a divider under it, not a panel.
- **A fleet commander's site run now reaches his fleet even if he never opened the fleets window.** The run window
  worked out which fleet it belonged to from a screen selection rather than from the fleet you are actually in, and
  the only thing that ever made that selection was the OPEN METRICS button. A commander who simply started a site
  therefore had no fleet as far as his own client was concerned: no group code was made, nothing was announced, and
  none of his fleet members saw the run appear. Which fleet you are in is now read from your membership, and your
  membership is refreshed when the app starts instead of only while the fleets window is open — so starting the
  client while already in a fleet is enough. A pilot who really is flying alone keeps every button and still
  announces nothing.
- **The fleet commander gets the run controls again, also without an in-game fleet.** Who commands a fleet was read
  from EVE itself, and EVE only answers that for a fleet you have actually formed in the game. An ordinary EVE
  Together fleet has no such link, so the answer never came — and the run window told the commander his controls were
  hidden because nobody knew who was in charge, two lines under a header naming him. It now reads the FC from the
  fleet's own roster, which is where you appoint one. If EVE Together and an in-game fleet disagree about who leads,
  EVE Together's roster is what counts.
- **A run of your own stays yours, even in a fleet somebody else leads.** Start, stop and discard were withheld from
  anyone who was not the FC, including for a run that was never shared with the fleet and that reaches nobody else's
  screen. Those runs keep all their buttons; only a run that carries a group code is the commander's to steer.
- **A fit you rename now carries its new name everywhere.** The name you type in *Edit fit details* was stored
  correctly, but the card in the fit browser and the header of the fit detail window both went on reading the name
  out of the fit's original import data — so the old name kept coming back. Both now show the name you gave it, and
  fall back to the imported one when you never renamed it. Renaming still leaves the fit's contents untouched: a
  renamed fit is the same fit, which is why the name is kept apart from them in the first place.
- **ISK figures across the app now show as whole numbers, the way EVE itself displays them** — no more
  `965.32 ISK` or `11,510,569.11 ISK` with cents nobody uses. A total is rounded once, from the exact sum of
  what makes it up, so it always matches what the rows underneath it add up to. Typing or pasting an ISK
  amount still accepts decimals; only the on-screen figure is rounded.
- **Mission ISK and Loyalty Point rewards written with just one thousands separator, like `360,000 ISK` or
  `1,150 Loyalty Points`, are now recognised** instead of being kept as unrecognised text — a mission reward
  is always a whole number, so there is no decimal it could otherwise be mistaken for.
- **A mission's bonus time window now keeps its minutes, not only its hours.** "Within 1 hour and 16 minutes"
  used to be read as a flat hour, rounding the window down; a window given only in minutes was not read at
  all. Both now count correctly.
- **A fleet mate's run pulled from a server no longer lands hours off.** Syncing runs read a time back out of
  a database column and treated it as local time instead of UTC — on a machine outside UTC (say, Central
  European Summer Time) that shifted a fleet mate's run by the reader's own time zone offset, and threw off
  the whole activity's shared duration along with it. Both directions of the sync now always read that time
  as UTC.

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
- A fleet card on the fleet overview now shortens its member list once there are more than six
  members, so a fifty-man fleet no longer turns the overview into one long scroll. The six it shows
  are the ones you are looking for: the fleet commander first, then your own characters, then
  external pilots, then the rest. Under them a line reads `+ 44 more` — with how many of those are
  external, since an external pilot has a row nowhere else in the client — and clicking it opens the
  rest right there on the card. How many pilots the fleet holds is always on the card, folded or not,
  and unfolding survives a refresh. A small fleet is unchanged: no extra line, no extra click.

### Fixed
- **Your characters no longer quietly lose their link to a server and have to be coupled again by hand.** When a
  server declined to renew this client's stored sign-in — which can happen after a server restart, after your PC
  wakes from sleep, or simply when several characters reconnect at the same moment — the app deleted that character's
  stored pairing. There was no way back from that except coupling the character again yourself, and nothing said it
  had happened; the character was just gone from the server. The pairing is now kept and retried, and the app says so
  while it lasts: the character's server chip turns red, a banner explains that reads from that server come back
  empty rather than failing, and a message appears the moment it starts. Renewals are also no longer allowed to
  collide with each other, which is what made a working pairing look expired in the first place.
- **The ESI badge now says when EVE is refusing a character's token.** If EVE started rejecting a token the app still
  believed was good, every call for that character failed while the badge stayed green — the app went by the token's
  own expiry time and never heard EVE's opinion. It now turns amber, says so, and renews the token instead of sending
  the refused one again.
- **Waking your PC from sleep now rebuilds the connections instead of waiting for them to notice.** Server links and
  ESI tokens are both stale after a nap; the app now checks both within seconds of coming back rather than up to a
  minute later, and in some cases not at all.
- A long warning banner at the top of the window no longer runs off the right edge — it wraps.
- **EVE's daily downtime no longer stops the app from following where your characters are.** While Tranquility is
  down the app holds back its own calls rather than hammering a dead API — but it counted each held-back call as a
  failed reading, and after two minutes of them it gave up watching, for the rest of the session. Downtime lasts
  longer than two minutes, so this happened every single day, and nothing started the watch again. The visible cost
  was a character whose location simply never appeared: start a client after downtime and the app no longer asked
  EVE where you were, because the answer arrives on the readings that had stopped. Calls the app held back itself
  are no longer counted as failures — nothing was asked, so nothing failed.
- Leaving the app open for an hour no longer breaks saving. Your login to a server is renewed
  every hour, and until now that only happened when the connection to that server dropped — so on
  a connection that simply stayed up, the login quietly went stale while the app still showed
  itself as connected. Fleets and fits kept arriving over that open connection, so nothing looked
  wrong; but the first thing you tried to *save* — a new fleet composition, a fit shared to the
  server — came back **"Not authenticated — pair with the server first."**, which was neither true
  nor something re-pairing would have fixed. The login is now renewed the moment the server stops
  accepting it, whether the connection dropped or not.
- The server chip beside a character (the cloud with the server's name) now turns **red** as soon
  as the app knows the pairing is no longer valid and only you can fix it, with a struck-through
  cloud and "session expired — re-pair" on hover. A link that is merely dropped or reconnecting
  stays amber, as it fixes itself. Previously an invalid pairing was something you found out about
  by trying to save.
- A lapsed pairing also raises a **banner across the top of the window** naming the server, and it
  stays there until the pairing is good again rather than fading after a few seconds. It has to:
  a list read against a server that no longer accepts your session used to come back **empty**
  instead of failing, so fleets, compositions and shared fits from that server quietly read as
  "there is nothing here" — indefinitely, and with nothing on screen to say otherwise.
- A list that could not be read no longer claims to be empty. The Compositions window said
  "No compositions shared on this server yet." whether the server held nothing or had refused the
  request outright; it now says it couldn't load, and passes on the server's own reason. The same
  applies to coupling a composition to a fleet, and to duplicating or pushing one — none of which
  will now act on a library it failed to read.
- **EVE Settings Sync** now finds your EVE folder on Linux by itself. EVE on Linux is the Windows
  client running under Steam's Proton (or plain Wine), so its settings live inside that prefix
  rather than where a Linux program would put them — and the tool, which only ever looked in the
  Windows location, opened empty and left you to hunt the folder down through
  `steamapps/compatdata/…/pfx/drive_c/users/steamuser/…` yourself. It now walks Steam's libraries
  (including a second disk, and the Flatpak install) to EVE's own prefix, falling back to
  `$WINEPREFIX` and `~/.wine` for an install outside Steam. There is also a new **AUTODETECT**
  button beside **BROWSE…**, so a game installed or moved after you first opened the tool can be
  picked up without restarting it. If nothing is found, nothing changes: the folder you set stays,
  and **BROWSE…** still points the tool wherever you like. Windows is unaffected — it is asked
  first, exactly as before.
- Removing, adding or repositioning a fleet member now updates **every** open screen showing that
  pilot, not only the screen you did it in. Removing someone in fleet metrics with the fleet
  overview open beside it used to clear the metrics card and leave the pilot sitting on the
  overview's; the same held between the roster window and the other two, and for a local
  (client-only) fleet nothing would ever have corrected it, since such a fleet sends no roster
  update. Fleet metrics, the fleet overview, the roster window and a member's popped-out DPS
  window now all follow the same change, wherever it was made — the pop-out of a removed member
  closes with their row rather than freezing on its last frame.
- A pilot added to a local (client-only) fleet now shows up everywhere that fleet is shown, instead
  of only in fleet manage. The fleet's card in the Fleets window listed only the characters signed
  in on this machine, so an **external** pilot — the only way to add someone who isn't signed in
  here — never appeared on it, and adding anyone at all did not refresh the card. The fleet metrics
  window, meanwhile, read its roster once when it opened: opening it again handed back the screen
  built before the pilot joined, and an external pilot sends no live data of their own, so nothing
  could ever fill the gap. That also quietly shrank the roll-up totals and the `WITH FC` badge's
  count, which showed as a complete figure. Metrics also gets its own window per fleet now, so
  opening metrics for a second fleet no longer re-shows the first fleet's.
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
