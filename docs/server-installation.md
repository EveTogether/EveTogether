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
| `ConnectionStrings__Sqlite` | no | Defaults to `Data Source=/data/eve-utils-server.db`. |
| `ConnectionStrings__MySql` / `__SqlServer` / `__PostgreSql` | depends | Connection string for the chosen provider. |
| `Server__Name` | no | Display name shown to clients. |
| `Server__HttpsPort` | no | HTTPS port inside the container (default `7443`). |
| `Server__AdminSeedPassword` | **yes** (outside Development) | Initial Blazor control-panel admin password. Change it after first login. |

The `/data` directory holds the SQLite database, TLS certificate, app log, ESI cache and auth store —
mount a volume there to persist it.

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

> Until the image is published, build locally instead: uncomment `build: .` in the compose file and run
> `docker compose up -d --build` (skip the pull). If you change `Server__HttpsPort`, match the compose port mapping.

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

- Everything persists in the `/data` volume — **back it up**. In keeping with the project's data-minimisation
  principle, the server stores tokens plus minimal coupling state; character data is cached ephemerally
  (honouring the ESI TTL), not warehoused.
- **Upgrade:** `docker compose pull && docker compose up -d`. Database migrations apply automatically on start.
