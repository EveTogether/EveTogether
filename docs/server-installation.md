# Server installation

The EVE Together server ships as a **Docker image only** — you run it yourself, nothing is hosted for you.
It comes with **no EVE credentials baked in**: every deployment registers its own EVE application and
supplies the Client ID and Secret. The server refuses to start outside Development without them.

## 1. Prerequisites

- **Docker** + Docker Compose.
- A **public HTTPS URL** for the EVE SSO callback (a domain with valid TLS — see [§5](#5-tls--reverse-proxy)).
- An **EVE account** to register a developer application.
- Optional: an external database (defaults to SQLite in the data volume).

## 2. Register an EVE application

At <https://developers.eveonline.com/> → **Manage Applications → Create New Application**:

- **Connection Type:** _Authentication & API Access_ (required to request ESI scopes).
- **Scopes:** those the server needs (e.g. `esi-fittings.read_fittings.v1`, fleet scopes, …). The running
  server publishes its requested scopes at `GET /api/server/scopes`.
- **Callback URL:** `https://<your-domain>/auth/eve/callback` — must match `Esi__CallbackUri` exactly.

Copy the **Client ID** and **Secret Key**.

## 3. Configuration

Configure via **environment variables**. Nested keys use a double underscore (`__`); arrays bind by index
(`Esi__Scopes__0`, `Esi__Scopes__1`, …).

| Variable | Required | Description |
|----------|----------|-------------|
| `ASPNETCORE_ENVIRONMENT` | — | `Production` (the image default). |
| `Esi__ClientId` | **yes** | EVE application Client ID. |
| `Esi__ClientSecret` | **yes** | EVE application Secret Key. |
| `Esi__CallbackUri` | **yes** | Public callback URL; must match the EVE application (`https://<domain>/auth/eve/callback`). |
| `Esi__Scopes__0`, `__1`, … | no | Requested ESI scopes. |
| `Esi__AllowedCharacters__0`, … | no | Characters allowed to pair, seeded on first start (enforced by default — see [§6](#6-first-run)). |
| `Database__Provider` | no | `Sqlite` (default), `MySql`, `SqlServer` or `PostgreSql`. |
| `ConnectionStrings__Sqlite` | no | Defaults to `Data Source=eve-utils-server.db`; a relative file is placed in the data directory. |
| `ConnectionStrings__MySql` / `__SqlServer` / `__PostgreSql` | depends | Connection string for the chosen provider. |
| `Server__Name` | no | Display name shown to clients. |
| `Server__HttpsPort` | no | HTTPS port inside the container (default `7443`). |
| `Server__AdminSeedPassword` | **yes** (outside Development) | Initial Blazor control-panel admin password. Change it after first login. |
| `EVEUTILS_SERVER_DATA_DIR` | no | Data directory. Set to `/data` in the image — leave it alone under Docker. |
| `Server__DataDirectory` | no | Data directory, used only when `EVEUTILS_SERVER_DATA_DIR` is unset. |
| `Server__AcceptNewIdentity` | no | `true` accepts a regenerated token-protector key — see [§7](#7-data-backups--upgrading). Never leave it on. |
| `ServerApi__AllowedOrigins__0`, `__1`, … | no | Browser origins allowed to call the REST API. Empty (the default) sends no CORS headers at all — see [§8](#8-the-read-only-rest-api). |
| `ServerApi__RateLimitPerMinute` | no | Requests per minute allowed to one API key, and to all keyless callers together (default `120`). |
| `ServerApi__KnownProxies__0`, … | no | Addresses whose `X-Forwarded-*` headers may be believed. Empty (the default) ignores them. |

### Data directory

The data directory holds the SQLite database, TLS certificate, token-protector key, app log, ESI cache and
SDE. It is resolved in this order:

1. `EVEUTILS_SERVER_DATA_DIR` — what the Docker image sets (`/data`); mount a volume there to persist it.
2. `Server__DataDirectory`.
3. Neither set: the per-user data folder — `%LOCALAPPDATA%\EveUtils.Server` on Windows,
   `$XDG_DATA_HOME/EveUtils.Server` (usually `~/.local/share/EveUtils.Server`) on Linux and macOS.

The default deliberately sits outside the build output, so a rebuild or a `dotnet clean` cannot take the
server's identity with it. A bare-metal installation from before this change kept its data in
`<build output>/data`; on the first start on the default the server **moves** that folder's contents to the
new location and logs what it moved. It does not do that when you point it somewhere explicitly with either
setting — move the files yourself in that case.

## 4. Run

### Docker Compose (recommended)

Use the [`docker-compose.yml`](../docker-compose.yml) in the repo root with a `.env` file beside it:

```dotenv
ESI_CLIENT_ID=your-client-id
ESI_CLIENT_SECRET=your-secret-key
ADMIN_SEED_PASSWORD=choose-a-strong-password
```

Set `Esi__CallbackUri`, `Server__Name` and the database settings in the compose file, then:

```bash
docker compose pull && docker compose up -d && docker compose logs -f
```

> Prefer to build from source instead of pulling the published image? Add a `docker-compose.override.yml`
> next to `docker-compose.yml` with `build: .` and `pull_policy: never` — Compose reads it automatically,
> so `docker compose up -d --build` builds your checkout instead. Neither the override file nor your `.env`
> is committed. If you change `Server__HttpsPort`, match the compose port mapping.

### docker run

```bash
docker run -d --name eve-together-server \
  -p 7443:7443 -v eve-together-data:/data \
  -e Esi__ClientId="your-client-id" \
  -e Esi__ClientSecret="your-secret-key" \
  -e Esi__CallbackUri="https://your-server.example.com/auth/eve/callback" \
  -e Server__AdminSeedPassword="choose-a-strong-password" \
  ghcr.io/evetogether/eve-together-server:latest
```

Instead of environment variables you can mount a read-only `appsettings.Production.json` at `/app/` with the
same keys (`Server`, `Database`, `ConnectionStrings`, `Esi`). Keep it out of version control — it holds your secret.

## 5. TLS & reverse proxy

The server serves a single HTTPS endpoint (gRPC over HTTP/2 alongside the Blazor panel over HTTP/1.1 via
ALPN). On first start it generates a **self-signed certificate** in the data directory; the desktop client
pins its fingerprint on first connection (trust-on-first-use), printed at startup:

```
Server TLS cert fingerprint (pin this during pairing): <fingerprint>
```

The **EVE SSO callback is browser-based**, so a self-signed cert triggers a browser warning there. For
production, run the server **behind a reverse proxy** (Caddy, nginx, Traefik, …) that terminates TLS with a
valid certificate and forwards to port `7443`. Point both `Esi__CallbackUri` and the EVE application's
callback URL at that public HTTPS address.

## 6. First run

- Set `Server__AdminSeedPassword` for the Blazor control-panel admin; sign in and change it afterwards.
- Note the TLS fingerprint from the log — desktop clients pin it when pairing.
- **Access control:** the pairing allowed-list is **enforced by default**, so seed the allowed character(s)
  via `Esi__AllowedCharacters__*` or add them in the control panel. **An enforced, empty list blocks everyone** —
  to run an open server (anyone who completes the EVE auth-flow can pair), switch to public-server mode in the panel.

## 7. Data, backups & upgrading

### The backup button

**Control panel → Backup.** One AES-256-encrypted `.zip` that rebuilds this server somewhere else: the whole
database plus `token-protector.key` and `server-cert.pfx`. The same page restores one. Use this rather than
copying the data directory by hand — it takes a consistent snapshot on any of the four database engines and
records who downloaded it.

- **Open it with 7-Zip or WinRAR**, or `7z x` on Linux. **Windows Explorer cannot open it**: Explorer only
  supports the old, broken ZipCrypto, and AES is a WinZip extension it never implemented. The encryption is
  standard either way, so you never need EVE Together itself to look inside your own backup.
- **You choose a password at download time and it cannot be recovered.** Without it the archive is permanently
  unreadable; with it, whoever holds the file can take over every linked character. Store it like the tokens.
- **Use a long password — at least 20 characters, and the panel will generate one for you.** The ZIP format fixes
  its key derivation at 1000 PBKDF2 rounds, which is next to no work per guess for anyone who steals the file.
  Length is the only defence left, which is why the minimum is what it is.
- **The archive does not carry your configuration.** The ESI client id and secret, the control-panel admin
  password and the database connection string are per-installation and stay out of it. Put those in place first on
  the new machine (§3), then restore. `esi-cache/` and the SDE are left out too — both rebuild themselves.
- **Restoring is destructive.** The database is dropped and rebuilt from the archive, and the TLS certificate and
  token-protector key are replaced. Anyone who paired after the archive was taken has to pair again. Before it
  drops anything the server writes an archive of its current state into the data directory under the same
  password, named `pre-restore-<timestamp>Z.zip`; that file is the way back if a restore goes wrong. It is
  kept, not cleaned up, because it may hold the only remaining copy of the previous token-protector key.
- **An archive from a newer version of EVE Together is refused**; an older one is accepted, and the migrations
  bring the schema forward on the next start. The archive has to come from the same database engine.
- **After a restore the server stops itself** so it comes back up on the restored data. Under Docker the restart
  policy does that; a bare-metal install has to be started again by hand.

### The data directory

- Everything persists in the data directory (`/data` under Docker), and backing it up as a whole still works. In
  keeping with the project's data-minimisation principle, the server stores tokens plus minimal coupling state;
  character data is cached ephemerally (honouring the ESI TTL), not warehoused.
- **`eve-utils-server.db` and `token-protector.key` belong together.** The key decrypts the stored ESI refresh
  tokens; restore them as a pair, from the same backup. A database without its key means every paired character
  has to pair again — there is no way back.
- `server-cert.pfx` is worth restoring too: without it the server presents a new TLS fingerprint and every client
  that pinned the old one has to pair again. `esi-cache/` and `sde/` rebuild themselves and need no backup.
- **The server refuses to start** when it has generated a new `token-protector.key` while characters are still
  paired — that combination means their refresh tokens can no longer be read. Restore the matching key, or, if the
  new identity is really what you want, start once with `--accept-new-identity` (or `Server__AcceptNewIdentity=true`)
  and pair every character again.
- **Upgrade:** `docker compose pull && docker compose up -d`. Database migrations apply automatically on start.

## 8. The read-only REST API

External consumers — your own site, a dashboard, tooling from fellow players — read server data over
`/api/v1`, guarded by an API key you mint under **Access → API keys** in the control panel. The API is
read-only: there is no endpoint that changes anything. `/health`, `/openapi/v1.json` and `/scalar` are public
and need no key, so a consumer can read the contract before it has one.

### Keys

The key is shown once, at creation. Only its prefix and a hash are stored, so a lost key is replaced, not
recovered. Give a key an **expiry** when you create it — the list shows when each key expires and when it was
last used, which is how you spot one nobody needs any more and revoke it.

Send the key in the `X-API-KEY` header. `?apikey=` works too, for browsers and embeds that cannot set a
header, but **proxies and CDNs routinely log query strings**, so a key sent that way can end up in logs
outside your control. Prefer the header wherever you can set one.

### Rate limiting

Each key gets `ServerApi__RateLimitPerMinute` requests per minute (default `120`) and is counted on its own —
one consumer running hot cannot slow another down. Callers arriving **without** a key share a single allowance
of the same size between them; they only ever get `401` anyway, and leaving them uncounted would make the
keyless path the one unlimited way in. Over the limit answers `429` with a `Retry-After`. `/health` and CORS
preflight requests are never limited.

### CORS — off unless you turn it on

By default the API sends **no CORS headers**, which is what server-to-server callers and `curl` need and what
keeps a public API from being read by any page that feels like it. Browser code on another origin needs that
origin allowlisted:

```
ServerApi__AllowedOrigins__0=https://your-dashboard.example
ServerApi__AllowedOrigins__1=https://another.example
```

List origins exactly (scheme, host, and port when non-standard). There is deliberately no "allow any origin"
setting.

### Behind a reverse proxy or tunnel

`X-Forwarded-*` headers are ignored unless you name the proxy that may send them, because anyone can send
them:

```
ServerApi__KnownProxies__0=127.0.0.1
```

Set this and the real client address reaches the logs; leave it and every request looks like it came from the
proxy.
