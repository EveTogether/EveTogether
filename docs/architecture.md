# Architecture

How EVE Together is put together: three core projects (`Client` / `Server` / `Shared`) plus migration
plumbing, CQRS with its own dispatcher, a server-side permission gate, a local + gRPC event bus, and two
per-character auth modes. Coding conventions live in [`AGENTS.md`](../AGENTS.md); this document is the
structural overview.

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
