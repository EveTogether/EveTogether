# AGENTS.md — instructions for contributors and AI assistants

This is the **single source** for how code is written in EVE Together. Humans and AI
assistants both follow it. `CONTRIBUTING.md`, the README and any repo-level AI config
(`CLAUDE.md`, editor rules) point here rather than restating the rules.

If you use an AI assistant to write code for this project, load this file first and hold it
to every rule below. **A pull request is judged against this document, not against the
assistant that produced it.**

---

## 1. What this project is

EVE Together is a **local-first**, **fully autonomous** ops-sec tooling suite for EVE Online
(fits, assets, skills, fleet sync, killboard). Two deployments from one codebase: an Avalonia
desktop **client** (local SQLite) and an optional, user-self-hosted **server** (Docker).

Fixed principles — do not violate:

- **No central server / no SaaS.** We host nothing for the user. The only central component is
  an optional, read-only update-check URL that carries no user data.
- **Open source as a trust requirement.** Users must be able to see exactly what is stored and
  sent. Transparency is the point, not a side effect.
- **Data minimization.** Persist as little as possible; prefer ephemeral cache that honours the
  ESI TTL over a permanent data warehouse. Each module decides what it keeps.
- **Self-written code only — never copy code from EVE Workbench (EWB) or other tools.** External
  tools may be studied as reference for *how* something works; the implementation here is our own.
- **Good ESI citizen.** Honour caching headers, request conditionally, never poll below the TTL,
  retry conservatively, respect rate limits. See §7.
- **Always track the latest ESI version.** Pin `X-Compatibility-Date`; when a newer version
  exists, bump it and move the affected models/mappings along. If a bump is a **breaking change**,
  call it out explicitly (what breaks, which call sites, impact) — never bump silently.
- **Open for extension.** Design so extension is easy: enums over booleans/magic strings, seams
  over hard-coding, optional fields/overrides reserved. *Reserve ≠ build* — lay the seam or enum,
  do not build unused machinery or UI.
- **Explicit character choice — no global "active character."** Actions that concern a character
  ask *which* character at the moment of the action (a picker). The character list is
  informative, not an action default. Never introduce a "set active character" precondition.

---

## 2. Language

- **All source is English** — identifiers, comments, XML-docs, log and exception messages,
  error codes, endpoint descriptions. No other language in source.
- **User-facing text is English** too (the audience is international).
- Commit messages are English (see §9). Issues and discussion may be in any language.

---

## 3. Solution layout

Three core projects plus migration plumbing and tests, all `net10.0`:

- **`EveUtils.Client`** — Avalonia desktop app (MVVM, CommunityToolkit), composition root, local SQLite.
- **`EveUtils.Server`** — Minimal-API + gRPC + Blazor control panel; multi-provider database.
- **`EveUtils.Shared`** — entities, the EF contexts, CQRS infra, messaging/identity, and **all modules**.
- **`EveUtils.Migrations.Client.Sqlite`** and **`EveUtils.Migrations.Server.{Sqlite,MySql,SqlServer,PostgreSql}`**
  — migration stacks; they reference only `Shared`.
- **`EveUtils.Server.Tests`**, **`EveUtils.Client.UiTests`** — xUnit (v3); UiTests use `Avalonia.Headless.XUnit`.

`Shared` top-level folders: `Cqrs/`, `Data/`, `Runtime/`, `Messaging/`, `Modules/`.

---

## 4. Architecture — modules + CQRS (no DDD)

Features are **vertical-slice modules**. **No DDD** layering (no Domain/Application/Infrastructure,
no aggregates/value-object ceremony).

A module is one self-contained folder under `EveUtils.Shared/Modules/<Name>/`:

```
Modules/Ships/
├─ Entities/        Ship.cs + ShipConfiguration.cs (IEntityTypeConfiguration co-located)
├─ Dtos/            ShipDto.cs            (plain classes, not records)
├─ Enums/           module enums live here, never in Dtos/
├─ Commands/        AddShipCommand.cs + AddShipCommandHandler.cs
├─ Queries/         GetShipsQuery.cs + GetShipsQueryHandler.cs
├─ Events/          ShipAddedEvent.cs
├─ Repositories/    IShipRepository.cs + Implementations/ShipRepository.cs
├─ ShipsModule.cs   AddShipsModule() — registers handlers via AddModuleHandlers(typeof(ShipsModule))
└─ ShipsWireEvents.cs
```

Rules:

- **Entity-owning modules live in `Shared`**, not in a host. The migration plumbing references only
  `Shared`, so the whole EF model must be reachable from `Shared`. Which host loads the module
  decides where the table lands.
- **Module boundaries:** a module talks to another module **only through its public contracts**
  (DTOs, and commands/queries via the dispatcher). Never reach into another module's `Entities/`
  or `Repositories/`. Entities stay internal; DTOs go out (to UI / gRPC).
- **One handler per command/query**, in the same folder, beside it.
- **Visibility:** command/query = `public sealed record` (the contract); **handler = `internal sealed`**
  (the dispatcher resolves it by reflection); validators separate and `internal sealed`.
- **Repository:** interface `public` (`I<Module>Repository`); implementation in
  `Repositories/Implementations/`, `internal sealed`.
- **Per-module registration.** `Add<Module>Module()` calls `AddModuleHandlers(typeof(<Module>Module))`,
  which scans only that module's namespace. `AddCqrs()` registers only the dispatcher. There is **no
  global multi-assembly scan** — it would register handlers of unloaded modules and fail DI validation.

### CQRS

- **Command** = mutation (no or minimal return). **Query** = read, no side effects. LINQ method syntax,
  `AsNoTracking` for reads.
- Command handlers return **`Result` / `Result<T>`** (see §6).
- DB access goes through **`IDbContextFactory` / `CreateDbContextAsync(ct)`** — a short-lived context
  per operation. Handlers never inject a `DbContext` directly. Contexts have **no public `DbSet`s**;
  the model is configured per module, repositories use `Set<T>()`.
- Host differences go through **`IRuntimeContext { ExecutionHost Host }`** (`ExecutionHost.Client | Server`),
  registered by each host at startup. **No `#if`** for host branching.

### Do NOT introduce these dependencies

This codebase deliberately uses **its own thin layers**. Adding the libraries below is the single
strongest "AI-generated, doesn't fit" signal and will be rejected:

| Instead of… | Use the in-repo equivalent |
|---|---|
| MediatR | the own dispatcher (`IDispatcher` / CQRS infra in `Cqrs/`) |
| AutoMapper | manual `static FromEntity()` / `ToEntity()` on the DTO (`ToEntity(existing)` for updates) |
| FluentResults / OneOf / ErrorOr | the own `Result<T>` + `Error` (`Messaging/`) |

DI uses Scrutor + marker interfaces (`IScopedService` / `ITransientService` / `ISingletonService`)
auto-registered with `publicOnly: false` so `internal` handlers register. Each project has its own
`AddXxx()` extension.

---

## 5. C# conventions

- **Naming:** standard .NET (PascalCase types/methods/properties, `I`-prefixed interfaces,
  `_camelCase` private fields, camelCase locals/params, PascalCase constants). Booleans read as
  predicates (`IsActive`, `HasScope`). **Private methods are `_PascalCase`** — a deliberate house rule.
- **`var`** only when the type is evident from the right-hand side (`new(...)`, cast, literal).
  If the type is not obvious (e.g. a method return), write the explicit type. Not "var everywhere".
- **Target-typed `new()`** when the variable has an interface/base type or it documents intent;
  `var x = new T()` when the type is already on the right. Don't mix both forms in one file.
- **`Async` suffix** on methods your own code awaits. Exception: framework entry points you don't
  call yourself (controller actions, event handlers, `ExecuteAsync(stoppingToken)`).
- **Nullable enabled everywhere. Do NOT add the null-forgiving `!` in new or changed code** — each
  one is a place the compiler can no longer protect you. Prefer, in order: `?? throw` (null = error)
  → `is not null` guard / early return → `?.` (null = do nothing). Don't anchor on surrounding code
  that uses `!`. The accepted exception is `null!` as the suppressor on EF navigation properties.
- **Guards / config invariants:** `configuration["X"] ?? throw new InvalidOperationException("X is not configured")`.
- **Modern idioms:** file-scoped namespaces (namespace == folder path); one type per file (a command
  and its handler together is fine); **primary constructors** on handlers/repos/behaviours/controllers
  (no ctor body, no `this._x = x`); `sealed record` for commands/queries/events/`Error`; `internal sealed class`
  for implementations; collection expressions (`= []`, `return []`); pattern matching (`is null` /
  `is not null` over `== null`, property patterns, switch expressions); `required` on mandatory
  properties; LINQ **method syntax always**; `IAsyncEnumerable` for streaming instead of buffering large
  lists; `sealed` on classes not meant for inheritance.

---

## 6. Errors, logging, guards

- **`Result<T>` / return values** for *expected* outcomes: not-found, validation failure, business-rule
  violation, external-call failure. These belong in control flow.
- **Exceptions** for *programming errors / truly exceptional* cases: a broken invariant, missing config
  (`?? throw`), unexpected infrastructure failure. **No exceptions for business flow.** Handlers wrap
  unexpected errors in `try/catch → Result.Failure`. A `catch (Exception)` that only rethrows or swallows
  is a smell.
- Every call returns payload + status + **structured messages** (severity + machine-readable code,
  e.g. `SCOPE_MISSING`, `RATE_LIMITED`, `PERMISSION_DENIED`, `SDE_OUTDATED`).
- **Guard clauses / early return** over deep nesting: exit at the top, keep a flat happy path. No guards
  for impossible cases, no double validation the pipeline already does.
- **Logging:** the ESI connector logs failures at Error level (surfaced in the in-app log window). No
  "entering method X" spam, no `Console.WriteLine` in production code.
- **YAGNI / DRY, right-sized:** no abstraction/interface/configurability "for later" without a concrete
  second user. Don't DRY at the cost of coupling — small duplication beats an abstraction that chains
  two layers together.

---

## 7. ESI good-citizen rules

- Honour the `Expires` header as the primary cache TTL; fall back to 1 hour; cache immutable data
  (killmails) effectively forever; add ±10% jitter to avoid cache stampedes.
- Conditional requests / ETag where available; never poll below the TTL; dedup; conservative concurrency.
- Retry discipline: **no blind retries.** 4xx → no retry (except 429, honour `Retry-After`). 5xx →
  limited backoff with jitter. **420 → hard stop.** Always read the ESI error body.
- Rate-limit split: authenticated = per `app:character`, public = per IP → bundle/cache public data
  through the server.
- Always send a descriptive **User-Agent with contact info**, and pin `X-Compatibility-Date`.

---

## 8. Definition of Done & review

A change is done only when **all** of these hold:

- Human-maintainable **without** an AI — clear structure, naming and build-up. Readability over cleverness.
- Compiles with **0 warnings** (no unjustified suppressions).
- Convention-conform with this document.
- Tests green where applicable; any new regression test is **proven red without the fix** (no fake green).
- **Error paths and edge cases** are covered, not just the happy path.
- **No secrets in the diff.**
- No anti-slop signals (§ below).
- Qodana-clean where Qodana runs (no new findings versus the baseline).

"Works on my screen" is not Done. "Build green" ≠ "works" ≠ "production-ready".

Reviews (manual or automated) check these **six dimensions, in order**:

1. **Correctness** — bugs, races, null/boundary cases, wrong assumptions.
2. **Convention conformance** — does it match this document?
3. **Anti-slop / production-ready** — see below.
4. **Reuse & simplification** — does it already exist? can it be simpler? (YAGNI, DRY, anti-splintering)
5. **Secrets** — none in the change.
6. **Tests** — do they actually prove something (red-without-fix, realistic mock shapes)?

### Anti-slop catalogue

On a match: **rewrite, don't patch.**

- **Inconsistency with the codebase (the strongest tell):** introducing MediatR/AutoMapper/FluentResults/OneOf
  where a thin own layer exists; exceptions for business flow instead of `Result<T>.Failure`; `== null`
  instead of `is null`; a classic ctor + `_x = x` instead of a primary constructor; block namespaces;
  `Startup.cs`; `Console.WriteLine`; mixing conventions within one file.
- **Fake-done / vibe-coded:** `NotImplementedException`, `// TODO`, empty stubs presented as done; a copied
  StackOverflow/tutorial pattern that doesn't fit the domain; tests that prove nothing (`Assert.True(true)`,
  happy-path-only, mock shape ≠ production shape).
- **Defensive over-engineering:** `try/catch` or null checks the flow already guarantees; `catch (Exception)`
  that only rethrows/swallows; guards for impossible cases; double validation; logging spam.
- **Naming:** meaningless `data`/`result`/`temp`/`item`/`obj`/`value`/`DoWork()`/`Process()`/`HandleData()`
  without domain context.
- **Comments & noise:** comments that repeat the *what*; comment-novels; `///` on implementations; AAA
  comments in tests; "Note that…" prose; emoji or non-English comments.

> Master test: *"Would the maintainer write it this way, and would it pass their own review?"* If no, it is
> slop, even if it compiles.

### Comments

Comments explain **why**, not **what** — intent, a trade-off, a non-obvious reason, or a numbered algorithm
step. Low density, compact and targeted, **no prose blocks** retelling the code. If you need to explain a lot,
fix the code or the naming instead. XML-`///` docs go on **interface methods only**, not on implementations.

---

## 9. Tests

xUnit + NSubstitute + FluentAssertions + EF InMemory. Test names: `Method_Scenario_Expected` or
`Context_Scenario_Expected`. No AAA comments. Per handler, cover success / not-found / exception /
validation-failure / edge. Command handlers: assert the state change and out-of-process interaction;
query handlers: assert the returned data. Don't mock the own bus — use the real `Result` flow. A new
regression test must be shown red without the fix before it counts.

If a project has no tests, flag it and record the decision in the project — don't decide unilaterally.

---

## 10. Commits

- **Entirely English** — both the type and the description.
- **No summary line at the top.** The body is `- {type}: {description}` bullets; each touched part gets its
  own bullet; nothing merged or omitted.
- Type is one of `added` / `changed` / `fixed` / `removed` / `refactored`. Description lowercase, no trailing
  period, active voice, one line per bullet — say *what* changed and *why* / what it fixes.
- **No `Co-Authored-By` and no AI attribution** of any kind ("Generated with …" etc.). Commits are in the
  author's name.

Example:

```
- fixed: prevent EsiFleetSyncService from polling a disbanded fleet by unlinking on a definitive NotFound
- refactored: move the VisibilityScope enum into Enums/ per the module convention
```

---

## 11. Build & test

```
make build      # dotnet build the solution
make test       # run all tests
make server     # run the server host
make client     # run the Avalonia client
make smoke      # headless data/CQRS smoke run
```

`PROVIDER=` and `ENVIRONMENT=` override the database provider and environment. The client also exposes
diagnostic flags (`--smoke`, `--esi-test`, `--sde-check`, `--grpc-ping`, …). Database migrations are
managed with `make migrate-add NAME=<Name> [SCOPE=all|client|server]` / `make migrate-remove` (wrapping the
scripts in `scripts/`); each migration change must build cleanly across all stacks.

---

## 12. Licence & attribution

- The project is licensed under **GNU AGPL-3.0** (`LICENSE`). The network clause means anyone who offers a
  modified server over a network must share the modified source.
- EVE Online material requires CCP's attribution. Show this verbatim wherever EVE material or CCP marks
  appear (app UI and website):

  > Material related to EVE-Online is used with limited permission of CCP Games hf by using official Toolkit.
  > No official affiliation or endorsement by CCP Games hf is stated or implied.

  Do not combine the EVE logo / CCP marks with other marks without CCP's written permission.
