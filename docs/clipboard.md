# Clipboard watch

`EveUtils.Client/Clipboard/` — an opt-in system that watches the system clipboard, recognises an
EFT fit or an EVE inventory listing, and hands the payload to whichever features subscribed. This
document records the reasoning the code cannot show on its own: the guarantees it makes, which
platforms are served and why the rest are excluded rather than pending, why recognition and parsing
are separate, and which measured properties of EVE's clipboard output the parser leans on.

It is deliberately **not** a `Shared` module: the clipboard is a desktop concern, like
`EveUtils.Client/Platform/`. Structural overview → [`architecture.md`](architecture.md).

**State today: nothing subscribes.** Nothing calls `Subscribe`, and `ClipboardCaptureParser` carries
no DI marker and is named nowhere but its own file and its own test. Copying an inventory out of EVE
therefore does nothing, and turning the switch on before a feature listens changes nothing at all —
the clipboard is not read while the subscriber list is empty. Two consumers are planned: registering
loot after an abyssal run, and offering to import a copied fit. That is the current state of the
system, not a hole in it: the recognition layer, the parser and the consumers were built as separate
steps on purpose.

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
`echo` — which ignores the payload handed to it on stdin. What crosses into the application is
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
where they have not opted in yet. If the process exits within the startup grace period the source
reports itself unsupported, `StartWatching` re-reads `IsSupported` after `Start`, and the status bar
turns to `CLIPBOARD UNSUPPORTED` instead of showing a switch that looks on.

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
A change notification can therefore arrive for a native Wayland copy the read cannot reach — the
right-hand column above. That is a limit of the reader, not of the source, and it lands in the
existing path: `GetClipboardTextAsync` returns nothing and the payload is dropped. In practice EVE
runs under Wine, which is an X11 client, so the game's own copies are the bottom row.

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
| Platform change source (injectable, so tests replace it) | `IClipboardChangeSource` → `WindowsClipboardChangeSource` / `WaylandClipboardChangeSource` / `UnsupportedClipboardChangeSource` |
