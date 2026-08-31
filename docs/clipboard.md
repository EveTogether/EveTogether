# Clipboard watch

`EveUtils.Client/Clipboard/` — an opt-in system that watches the system clipboard, recognises an
EFT fit or an EVE inventory listing, and hands the payload to whichever features subscribed. This
document records the reasoning the code cannot show on its own: the guarantees it makes, which
platforms are served and why the rest are excluded rather than pending, why recognition and parsing
are separate, and which measured properties of EVE's clipboard output the parser leans on.

It is deliberately **not** a `Shared` module: the clipboard is a desktop concern, like
`EveUtils.Client/Platform/`. Structural overview → [`architecture.md`](architecture.md).

**State today: two features subscribe.** `ClipboardFitImportOffer` offers to import a copied fit, and
`ClipboardSignatureOffer` (ET-79) shows what the SDE knows about a copied cosmic signature or
anomaly. `ClipboardCaptureParser` still carries no DI marker — the two subscribers above call the
shape-specific static parsers directly, and it exists for a future consumer that wants the parsing
layer through DI. Registering loot after an abyssal run remains planned but unbuilt.

## The guarantees

This is the one feature that can see everything the user copies, so what it does *not* do is the
design. Each guarantee below is enforced in code, not merely intended.

| Guarantee | Where |
|-----------|-------|
| **Off unless switched on.** Setting `clipboard.watch`; absent or anything but `"true"` means off. | `ClipboardWatchService.ReadEnabledAsync` / `InitializeAsync` |
| **Visibly off from startup**, without opening settings: the always-visible status bar reads `CLIPBOARD OFF`, `CLIPBOARD WATCHING` or `CLIPBOARD UNSUPPORTED`. | `MainWindowViewModel._ApplyClipboardState` |
| **Not read at all while nothing subscribes.** An empty subscriber list returns before the clipboard is touched, so the payload is never materialised. | `ClipboardWatchService.InspectAsync` |
| **Off means not read, not read one last time.** Stopping the change source holds off new notifications but not one already posted to the UI thread, so `InspectAsync` re-checks `IsWatching` on entry. | `ClipboardWatchService.InspectAsync` |
| **Unrecognised material is dropped where it is read** — not stored, not buffered, not logged, not attached to an error report. | `ClipboardWatchService.InspectAsync`, immediately after `Recognise` |
| **No raw clipboard text leaves the process.** The two log statements in the path carry an exception, a feature name and a shape — never the payload. It is not handed to the local API server either. | `ClipboardWatchService.InspectAsync` |
| **The disclosure cannot drift.** Settings → Privacy & Sharing lists the features listening by reading the live subscriber list (`Consumers`), not a hand-maintained one — which is why it currently says, in as many words, that nothing is. | `ClipboardWatchService.Consumers`, `SettingsWindow.ApplyClipboardDisclosure` |

A failed read is a dropped payload, not an error worth surfacing: Windows hands the clipboard to
one process at a time and the application that just copied can still hold it, so
`DialogService.GetClipboardTextAsync` returns `null` on `COMException`.

Subscriber names are user-visible — they appear in the disclosure — so `Subscribe` takes a feature
name, not a class name.

## Per platform, and what is still an exclusion rather than a to-do

Windows needs no polling: a message-only window (`HWND_MESSAGE`) registered with
`AddClipboardFormatListener` receives `WM_CLIPBOARDUPDATE` whenever the clipboard changes. The OS
pushes, so there is no interval to tune and nothing to fight over with a clipboard manager. The
window lives on its own thread with its own message pump, because a window created on the UI thread
would deliver its messages into Avalonia's loop.

Linux on Wayland needs no polling either. `WaylandClipboardChangeSource` keeps one
`wl-paste --watch echo` running and treats each line it writes as one change. `wl-paste` speaks the
compositor's data-control protocol, which is a real push, and the command it runs per change is
`echo` — which ignores the payload handed to it on stdin. (One user action is not always one line:
clearing the clipboard measurably produces two. Harmless — both reads find nothing recognisable and
drop it — but "a line per change" is the honest phrasing, not "a line per copy".) What crosses into the application is
therefore a bare line: an event, with no previous content kept anywhere and nothing compared. Two
consequences worth naming: **byte-identical copies each raise a change** (a content diff could not
do that), and the line `wl-paste` writes for the clipboard that was *already there* when it starts
is dropped, because the user did not copy it while watching.

The alternative — speaking the protocol directly through a libwayland binding — is a few hundred
lines for the same event. `wl-paste` costs one child process and a dependency on `wl-clipboard`
being installed, and both failure modes are loud rather than silent: the process either starts or
exits at once.

### What stays unsupported

**macOS** and **Linux without Wayland** (an X11-only session) report `IsSupported == false`, and
the UI says so instead of leaving a toggle that silently does nothing. Each has a known route —
macOS through `NSPasteboard.changeCount`, a monotonic counter with no content to remember, and X11
through XFixes selection notifications — and neither is built, because neither could be measured on
the machine this was written on. An untested platform source is more expensive than none: it turns
a visible "unsupported" into an invisible "does nothing".

**Wayland without data-control.** GNOME's Mutter does not offer the protocol. This cannot be known
without trying, and the only probe that does not read the clipboard is starting `wl-paste` itself —
so the probe *is* the start, and it runs when the user switches on rather than in the constructor,
where they have not opted in yet. The **first line does double duty**: a desktop that cannot notify
makes `wl-paste` exit without writing one, so its arrival is the capability answer, and its payload
was copied before watching began, so it is dropped either way. Nothing is waited for on the calling
thread — that thread is the UI's.

### A source that falls away must not fall silent

The same failure has a second half. `wl-paste` can also disappear *after* a good start: a compositor
restart, an OOM kill, a `wl-clipboard` upgrade underneath a running app. The pump then reaches
end-of-stream and returns, and without a signal `IsWatching` would stay `true` for the rest of the
session — a switch that looks on while nothing will ever arrive again, which is the exact state
`UnsupportedClipboardChangeSource` exists to prevent.

So `IClipboardChangeSource` carries a second event, `SupportChanged`, raised off the UI thread when
a source learns it cannot notify or stops being able to. `ClipboardWatchService` drops `IsWatching`
and raises `StateChanged`, and the status bar follows. Windows declares it and never raises it: it
knows on construction and does not change its mind.

**The watcher is deliberately not restarted.** Restarting needs a policy — how often, how fast, when
to give up — that nothing here asks for, and a watcher that died because the protocol went away
would restart-loop. Stopping honestly leaves the switch usable, so switching on again *is* the
retry, with no policy to tune.

**And it is stopped on the way out.** A child process outlives the parent that spawned it, so
`App.OnFrameworkInitializationCompleted` disposes the watch service on `desktop.Exit`; without that,
every run of the application would leave a `wl-paste` behind watching a clipboard for nobody. The
Windows source never needed this — it is in-process.

Polling on content remains ruled out everywhere. A poller cannot tell "changed" from "still the
same" without keeping state about the previous payload — including payloads it did not recognise.
Either it drags the same password back into the process on every tick, or it remembers a hash of
unrecognised material, and a hash of a password is derived from that password. Both collide
head-on with the first guarantee above. Anyone reaching for a poller to "finish" a platform is
proposing to break the feature's central promise; if the promise is ever renegotiated, that is the
conversation to have first.

### Measured on KDE Plasma, Wayland, Fedora 44 (2026-08-31)

The reason this is measurement rather than reading. `wl-paste --watch` was compared against an
XFixes listener on the X11 `CLIPBOARD` selection, three rounds per origin, alongside what an
ordinary X11 client managed to *read* afterwards:

| copied by | `wl-paste` fired | XFixes fired | X11 read succeeded |
|---|---:|---:|---:|
| a native Wayland application | 3/3 | 1/3 | 1/3 |
| an X11 application (through XWayland) | 3/3 | 3/3 | 3/3 |

Three things follow. **XFixes is not a Linux answer** — with `DISPLAY` set it looks like one,
because XWayland is running, but it misses native Wayland copies unpredictably rather than
consistently, which is worse than not working. **This compositor advertises
`ext_data_control_manager_v1`**, the standardised successor to `zwlr_data_control_manager_v1`;
`wl-clipboard` 2.2.1 speaks both. And **Avalonia reads through X11**: `Avalonia.Desktop` 12.1.1
depends on `Avalonia.X11` and ships no Wayland backend, so the application is an XWayland client.
A change notification therefore arrives for a native Wayland copy that the toplevel's read cannot
reach — the right-hand column above.

**This was not a corner case, and it is now fixed.** Measured on 2026-08-31 with a fit copied out of
Chromium: `wl-paste` returned all 555 bytes, the X11 `CLIPBOARD` selection had **no owner at all**,
and four X11 reads in a row returned nothing. Every fit copied from a browser was noticed and then
silently dropped, because the notification came over the compositor's data-control protocol while
the reading went through Avalonia's X11 clipboard. Two different worlds.

So reading moved onto the same seam as notification: `IClipboardChangeSource.ReadTextAsync` returns
the text over the channel that source is notified on, or null to leave the reading to the toplevel.
The Wayland source runs a one-shot `wl-paste`; Windows returns null and the toplevel reads as before.
A consumer never sees the difference — it subscribes and receives a `ClipboardCapture`.

And a read that comes back empty no longer ends in silence: it is indistinguishable from "nothing
recognisable was copied" otherwise, so the fact (never the payload) is logged.

## Recognition and parsing are separate on purpose

`ClipboardShapeRecogniser` decides on **shape alone** and deliberately does not parse. It runs on
every copy the user makes all day, so it has to be cheap, and it is the gate in front of the drop
rule, so it has to be strict. Parsing runs only on something already recognised, in
`ClipboardCaptureParser`, and only when a subscriber asks for it.

- **Fit** — the first non-empty line matches `^\[[^\[\]\r\n]+,[^\[\]\r\n]*\]$`, EVE's own
  `[Ship, Fit name]` export header. The existing `IFitTextImporter.Detect` accepts any text
  starting with `[`, which is right for a paste window — the user has already said "this is a fit" —
  and far too loose for a hook that sees every copy. There is no second fit parser: `ParseFit`
  routes the raw text to `IFitTextImporter.Import`.
- **Inventory** — at least two non-empty rows, at least one tab, and the same tab count on every
  row. An inventory copy carries whichever columns the window happened to show, so there is no
  header row to key on; the only stable signal is the table shape.
- **Signature** (ET-79) — every non-empty row has exactly six tab-separated fields, the first
  matching the (unverified — see below) signature-id pattern and the fifth ending in `%`. Checked
  before the inventory rule, because several signature rows also carry an equal tab count per row
  and would otherwise be claimed as `Inventory` first. No word from the EVE UI is used as an anchor,
  so the shape is recognised the same way regardless of the client's language.

**A stricter shape rule is not available from the material.** There are no negative captures — no
spreadsheet selection, no web-page table — so a pasted spreadsheet with a consistent tab count is
recognised as `Inventory` today. That has no consequence while nothing subscribes, and the inventory
parser is the second line of defence: it returns an empty list rather than guessing when it cannot
identify a name column (see the limit below). Any tighter threshold would be chosen, not measured.

## What the inventory parser leans on

Measured 2026-08-30 on five captures from a running EVE client — two EFT fits and the inventory
window's detail, list and icons views. This is the expensive knowledge: it costs a live client and a
hangar to re-acquire.

| Property | Evidence | What the parser does about it |
|----------|----------|-------------------------------|
| **Column order and count are not fixed** | The player chooses which columns the inventory window shows, so neither is a constant — no capture with a different order was taken, but the width already varies in this material: the detail and list views are byte-identical (same MD5) at 6 tabs per row, the icons view has 1. | Nothing is read by position. Every column is identified by the shape of its contents. A reordered table is proven to parse identically in `ClipboardCaptureParserTests`. |
| **Units are the anchor** | Volume fields end in `m3`, price fields in `ISK`, a quantity is a bare whole number, and the name column is the one that is always filled. | `FindUnitColumn` takes a column only if *every* non-blank field carries the unit; `FindQuantityColumn` takes the first column with any parseable whole number; `FindNameColumn` excludes blank, unit and numeric columns. |
| **Numbers are in the player's locale** | `42.237,65 ISK` and `0,10 m3` — dot as thousands separator, comma as decimal. Another player supplies `42,237.65`. | **Never `InvariantCulture`, and never a culture at all.** `TryParseLocalNumber` derives the form from the text: where the last separator sits, how many digits follow it, and whether the groups are valid threes. One or two trailing digits means a decimal separator; more than one separator with three trailing digits means grouping only. Undecidable input yields **no value**, never a guess — a culture-blind `Parse` would silently return a wrong number, which is the worst outcome available. |
| **Empty fields are normal, including at the end of a row** | `Baryon Exotic Plasma S Blueprint→→Exotic Plasma Charge Blueprint→→→0,01 m3→` — no quantity, no price, and the row ends on a tab. 9 of the 40 detail rows end on a tab. | Blank fields are skipped when identifying a column, and every value except the name is nullable on `ClipboardInventoryItem`. |
| **The icons view has only two columns** | Name, tab, quantity — and the quantity may be blank. | Column *count* is never a criterion, in either the recogniser or the parser. An "at least three columns" rule would throw away a valid inventory. |
| **Line endings are CRLF** | All five captures: one carriage return per line break, and the last row unterminated (the three inventory files carry 39 of each over 40 rows). | Rows are split on `\n` with a `TrimEnd('\r')`. |

## The acknowledged limit

`FindNameColumn` separates the item name from the group name by cardinality: the name column is the
text column with the most distinct values, accepted only if it has **at least twice** as many
distinct values as the runner-up. Otherwise the parser returns an empty list rather than risk
labelling a group as an item.

That 2× threshold is **chosen, not measured**. It rests on a single observation — the detail capture
had 39 distinct names against 10 distinct groups over 40 rows — and it is known to be wrong in the
conservative direction: a valid selection of 15 names against 8 groups is rejected, because 15 is
under 16. The failure mode is a refusal, not a mislabelling, which is the right way round; but the
number will need real material before it can be defended. The same note lives as a comment on the
returning line in `ClipboardInventoryParser.FindNameColumn`.

## Where it is wired

| Piece | Where |
|-------|-------|
| Started once the UI is up (the clipboard is read through the toplevel) | `App.OnFrameworkInitializationCompleted` → `ClipboardWatchService.InitializeAsync` |
| Status bar state, followed live via `StateChanged` | `MainWindowViewModel` |
| The switch and the disclosure | `SettingsWindow` → Privacy & Sharing |
| Reading the clipboard text | `IDialogService.GetClipboardTextAsync` |
| Stopped on the way out, because a child process outlives its parent | `App.OnFrameworkInitializationCompleted` → `desktop.Exit` |
| Reading the clipboard where the platform needs its own channel | `IClipboardChangeSource.ReadTextAsync` → `WaylandClipboardChangeSource` |
| Platform change source (injectable, so tests replace it) | `IClipboardChangeSource` → `WindowsClipboardChangeSource` / `WaylandClipboardChangeSource` / `UnsupportedClipboardChangeSource` |
| Signature detection, resolved before the watch starts (ET-79) | `App.OnFrameworkInitializationCompleted` → `ClipboardSignatureOffer` |

## Open verification (ET-79)

The signature-id pattern (`ClipboardShapeRecogniser.SignatureId`), the full set of scan-window groups
and the "not fully scanned yet" boundary are read from three external parsers, not from a live EVE
client — there was none running when this was built. They need checking against real captures, in
English and in a non-English client, before the id anchor is tightened or loosened. Full reasoning
and what exactly to verify: `tickets/ET-79-*.md` in the Depot `eve-together` project, §7.
