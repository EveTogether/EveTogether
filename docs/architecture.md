# Architecture

How EVE Together is put together: three core projects (`Client` / `Server` / `Shared`) plus migration
plumbing, CQRS with its own dispatcher, a server-side permission gate, a local + gRPC event bus, two
per-character auth modes, and the client-only clipboard watch. Coding conventions live in
[`AGENTS.md`](../AGENTS.md); this document is the structural overview.

## Projects (10, .NET 10, `.slnx`)

| Project | SDK | Role |
|---------|-----|------|
| **`EveUtils.Shared`** | `Sdk` | `Cqrs/` · `Data/` · `Runtime/` · `Messaging/` · `Identity/` · `Logging/` · `Protos/` · **`Modules/`** (all vertical slices) |
| **`EveUtils.Client`** | `Sdk` (WinExe) | Avalonia desktop app + composition root (`ClientServices`), ESI connector, gRPC client |
| **`EveUtils.Server`** | `Sdk.Web` | Minimal-API **+ Blazor Server admin UI + gRPC + SignalR** on one Kestrel TLS endpoint |
| `EveUtils.Migrations.Client.Sqlite` / `.Server.{Sqlite,MySql,SqlServer,PostgreSql}` | `Sdk` | migration plumbing; reference only `Shared` |
| `EveUtils.Client.UiTests` / `EveUtils.Server.Tests` | `Sdk` | xUnit test projects (the client tests run headless Avalonia) |

> Namespaces remain `EveUtils.*` for now.

## Vertical-slice modules

Each feature is one self-contained folder with everything it needs, under `EveUtils.Shared/Modules/`
(see [`AGENTS.md`](../AGENTS.md) §4 for the full convention):

```
Modules/Ships/                 (canonical reference)
├─ ShipsModule.cs              # AddShipsModule(): repos + AddModuleHandlers() + AddModulePermissions()
├─ Entities/                   # EF entities + IEntityTypeConfiguration — INTERNAL to the module
├─ Dtos/                       # public contract (UI/gRPC) — entities never leave the module
├─ Repositories/               # data access via IDbContextFactory<SharedDbContext>
├─ Queries/  + Commands/       # read / write → Result<T>
├─ Events/                     # IntegrationEvent<T> payloads (+ [RequiresPermission] where needed)
└─ <Name>Permissions.cs        # module.action codes for the gate
```

**Modules:**

| Module | What it does |
|--------|--------------|
| `Ships` | canonical reference module |
| `Esi` | EVE SSO/OAuth (PKCE, JWT validation), per-character encrypted token store, rate-limit monitor, **scope registry** (`EsiScopeTarget` Client/Server/Both) |
| `Fittings` | local + server-shared fits (`SharedFit`), ESI import, share/delete via gRPC, `fit.sync`/`fit.manage` permissions |
| `Fleet` | fleets, compositions/doctrines, roster, and ESI fleet-coupling |
| `Dogma` | fit stat-calculation engine (DPS, EHP, capacitor, resource usage) |
| `Sde` | EVE Static Data Export import + lookups |
| `Gamelog` | live gamelog tailing, combat parsing, DPS aggregation |
| `Skills` | character skills + training queue |
| `Implants` | character implants |
| `Market` | item price lookups |
| `Messaging` | inbox + server→client message queue |
| `Permissions` | code-derived permission registry + toggles (the app-permission gate) |
| `ServerAuth` | pairing state, issued sessions, session refresh + cleanup |
| `AdminAuth` | Blazor control-panel users, roles and RBAC |
| `Settings` | client-only preferences |
| `Sync` | server-only sync logs |

A module that **owns EF entities lives in `Shared`** — the migration projects reference only `Shared`,
so the model must be reachable from there. Which host loads a module decides where the table lands; most
register automatically through the shared marker-scan.

## CQRS (own dispatcher, no MediatR)

`Shared/Cqrs/`: `IQuery<T>` / `ICommand` / `ICommand<T>` + handlers, `IDispatcher`/`Dispatcher`.

- **Handlers registered per module:** `AddXxxModule()` calls `AddModuleHandlers(typeof(XxxModule))`
  (scans only that module's namespace). A host gets **only** the handlers of the modules it loads —
  no unused/unresolvable registrations (avoids DI-validation crashes).
- `AddCqrs()` registers the dispatcher.

## Permission gate (`[RequiresPermission]`)

- Commands/queries/events carry `[RequiresPermission("module.action")]`.
- `PermissionGateDispatcher` decorates the dispatcher; `IPermissionRegistry` is **code-derived** at
  startup (modules report their codes via `AddModulePermissions(catalog)`), with startup validation.
- `IAccessPolicy` decides access: v1 = `OwnerAllPolicy` (everything for the owner); the server uses
  `ToggleablePolicy` (per-permission on/off via persistent toggles).
- **Two-layer gate, enforced server-side:** the **event bus** checks `[RequiresPermission]` before
  remote-forwarding and rerouting — a modified open-source client cannot bypass the gate. The
  client-side check is deliberately cosmetic. Examples: `fit.sync` (share/sync), `fit.manage`
  (server-side delete of shared fits).

## Identity & event bus

- **`Identity/`** — `Character` (first-class, name-keyed, `GrantedScopes`), `ICharacterRegistry`,
  `IPrincipalAccessor` (`CharacterId` = principal for routing + gating).
- **`Messaging/`** — uniform `Result`/`Result<T>` (status + `ResultMessage` list), `MessageCodes`
  (`SCOPE_MISSING`/`RATE_LIMITED`/…); endpoints translate failure to `400` + messages.
- **Event bus** — `IEventBus` + `InProcessEventBus` (singleton). Events derive from `IntegrationEvent<T>`
  (`EventId`/`CharacterId`/`Timestamp`/`EventType` + payload). One `PublishAsync(evt, EventTarget)` with
  flags `Local`/`Remote`/`Both`. **Remote** goes through `IRemoteEventTransport` →
  `GrpcRemoteEventTransport` (client) ↔ `EventBusStream.Attach` bidi stream (server). Auth-gated.

## Change signals

Every state-changing command handler publishes **one module signal** once its write succeeded — the rule is in
[`AGENTS.md`](../AGENTS.md) §4. It exists because the alternative, announcing by hand at whatever edge a change
passes, kept missing edges: five bugs on one day (ET-10, ET-11, ET-20, ET-360, ET-378) were each a change nobody
heard about. Runs had it right since ET-222; ET-379 makes it the rule for every module.

```
handler ──(write ok)──► <Module>ChangedEvent (Local) ──► ChangeFeed<TEvent> ──► screens (UI thread, batched)
                                                    └──► relay subscriber (per host) ──► wire, to its audience
```

- **The signal** is `<Module>ChangedEvent` — the id of what changed plus a kind enum (`RunsChangedEvent`,
  `FleetChangedEvent`, `CompositionChangedEvent`). One per module or sub-module, not one per command: a screen
  subscribes once and every new command reaches it without a new pairing.
- **Local only from the handler.** The same handler runs on the client (local fleets, the local library) and on the
  server, and only the host knows who else must hear it. So each host has a **relay subscriber** on the signal:
  - *server* — a relay (e.g. `FleetChangeAnnouncer`) subscribes and pushes the change over the bus stream to its
    audience (roster, or every connected character for a listed fleet);
  - *client* — `ServerConnection` republishes a server-sourced event on the local bus, stamped with its server, so
    a screen hears a server change and a local one through the same signal.
- **Echo rule.** The server relays to the acting client as well; a client does not publish for a change it made
  through a server, it hears it back like everyone else. One source per change: no double reload, no "did I already
  publish this" bookkeeping, and the client never announces a change the server then refused.
- **Subscribers never work inline and never throw.** `InProcessEventBus` awaits each subscriber inside the
  publishing command, after the commit — a slow subscriber holds the command up, a throwing one turns a saved change
  into a reported failure. Client screens therefore listen through `ChangeFeed<TEvent>`
  (`EveUtils.Client/Messaging/`): the bus callback only records the change; a 250 ms window folds a burst into one
  batch, delivered on the UI thread, one reload at a time per screen. `RunChangeFeed` is the Runs module's feed on top
  of it.
- **Enforced by `CommandSignalCoverageTests`.** A reflection half fails for any command in `Shared` that has neither a
  scenario proving it signals (`RunsChangedSignalCoverageTests` holds the Runs ones), nor a reason on the exemption
  list, nor a ticket on the known-gap list; a behaviour half runs each scenario against a real store and bus. The
  known-gap list is a ratchet: `KnownGapCount` only goes down.

**Rejected:** a *dispatcher behaviour* that publishes after every command — it knows neither the id nor the audience,
signals falsely on idempotent no-ops and duplicates on nested dispatch. An *EF `SaveChanges` interceptor* —
`ExecuteUpdate`/`ExecuteDelete` bypass the change tracker, it sees rows rather than meaning, fires on high-frequency
writes too, and risks echo.

**Where the code does not follow it yet** (tracked under epic ET-379):

- Fleet structure, roster and invite commands and all composition commands publish nothing; the server announces
  them by hand in `FleetsGrpcService`, the client through `CompositionChangePublisher` and
  `FleetRosterWatch.Announce` (ET-381).
- Compositions break the echo rule both ways: the server leaves the acting character out of `composition.changed`
  (`FleetsGrpcService.AnnounceCompositionChangedAsync`), and the client publishes for a change it made on a server
  (ET-381).
- Fittings, Messaging, Settings, ApiKeys and the remaining modules: a signal or a reasoned exemption (ET-382).
- Writes outside the dispatcher — `DataAdminService`, `FleetCleanupRunner`, `ClientFleetService.AddLocalCharacterAsync`
  — are invisible to the test (ET-383).

## Auth — two per-character modes

- **Mode A (local)** 🏠 — the client does the EVE SSO itself; the token stays **client-side, encrypted**.
  Exchange = **PKCE + confidential** with the bundled app (EVE rejects PKCE-public-only). Callback = a
  fixed loopback.
- **Mode B (synced)** ☁️ — the token lives **on the server** (encrypted at rest); all ESI calls go through
  the server (proxy/cache). **Pairing** with `pairing_id` + secret + `oauth_state`; the SSO redirect goes
  straight to the server callback. Core principle: the client is untrusted — the server derives identity
  **only** from the EVE-signed JWT it fetches itself.
- **gRPC + TOFU** — the server generates a self-signed cert; the client pins the fingerprint at pairing
  (trust-on-first-use). gRPC + Blazor + SignalR share one Kestrel HTTPS endpoint (`Http1AndHttp2`, ALPN).
  Reconnect = silent refresh via a session-refresh token, with backoff.

## The three contexts

```
SharedDbContext (abstract, not injectable)  → Ships + Esi + Fittings + ServerAuth (where loaded)
 ├─ ClientDbContext → + Settings, Gamelog   → SQLite
 └─ ServerDbContext → + Sync                → multi-provider (Database:Provider)
```

Contexts have **no public `DbSet`s**; they build the model via `XxxModule.ConfigureModel(...)` and
repositories reach tables through `Set<T>()`. Data access is via **`IDbContextFactory<SharedDbContext>`**
(a short-lived context per operation).

## Clipboard watch (client-only)

`EveUtils.Client/Clipboard/` — not a `Shared` module: the clipboard is a desktop concern, like
`Client/Platform/`. An opt-in `ClipboardWatchService` recognises an EFT fit or an EVE inventory
listing on the clipboard and hands it to subscribed features; `ClipboardShapeRecogniser` decides on
shape only, `ClipboardCaptureParser` parses on request. Windows pushes changes through a
message-only window (`AddClipboardFormatListener`/`WM_CLIPBOARDUPDATE`); Linux and macOS report
themselves unsupported rather than poll.

The guarantees are the design here — off unless switched on, not read while nothing subscribes,
unrecognised payloads dropped unread and never logged — as is the reason polling is excluded rather
than pending, and the measured properties of EVE's clipboard output the parser depends on. All of
that, plus the state today (**no consumers yet**), is in **[`clipboard.md`](clipboard.md)**.
