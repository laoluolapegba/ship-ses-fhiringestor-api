# SHIP SES Ingestor — Deployment & Operations Guide

Audience: the team deploying this service for the first time. This document lists
**everything the Ingestor expects in its environment**, how configuration is loaded,
how to wire up its external dependencies, and how to read the startup logs when
something is misconfigured.

---

## 1. What it is and what it depends on

- **Runtime:** ASP.NET Core Web API on **.NET 9**, designed to run as a Linux container in Kubernetes.
- **Entry assembly:** `Ship.Ses.Ingestor.Api.dll`.
- **External dependencies (all required for normal operation):**

| Dependency | Used for | Failure symptom if missing |
|---|---|---|
| **MongoDB** | Persisting ingested FHIR sync records | App fails to start (no connection string) or `/health` returns 503 |
| **OIDC IdP (Keycloak)** | JWT bearer validation | App fails to start (Authority missing) or all requests `401` |

Per-client HMAC signing secrets are read from configuration (`AppSettings:Clients`) once at startup — see §3.5. If no clients are configured, every signed request is rejected with `401`.

---

## 2. How configuration is loaded

Configuration is layered; later sources override earlier ones:

1. **`appsettings.json`** — baked into the image. Holds **non-secret defaults only** (header names, clock skew, rate-limit policy, log config). It currently also contains **development** values for Authority/Audience/Mongo — these **must be overridden** in any real environment.
2. **`appsettings.{ASPNETCORE_ENVIRONMENT}.json`** — optional per-environment overrides.
3. **Environment variables** — the primary mechanism for deployment. Use these for everything environment-specific and **all secrets**.

### Two env-var naming conventions (important)

- **.NET configuration keys** use a double underscore `__` as the section separator.
  `AppSettings:Authentication:Authority` → `AppSettings__Authentication__Authority`
- **Array entries** use a numeric index segment, e.g. the first HMAC client's signing secret is
  `AppSettings:Clients:0:HmacSecret` → `AppSettings__Clients__0__HmacSecret`.

> Never put HMAC client secrets or the Mongo password into `appsettings.json`. Use env vars / Kubernetes Secrets.

---

## 3. Environment variable reference

Legend: **Required** = service will not function without it (some hard-fail at startup, noted below).

### 3.1 .NET runtime / hosting

| Variable | Required | Default | Description |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | `Development` enables Scalar API reference + HTTPS redirect + developer exception page. Use `Production` in prod. |
| `ASPNETCORE_URLS` | Recommended | — | Listen address, e.g. `http://+:8080`. With TLS terminated at the ingress, listen on HTTP. |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | Only if terminating TLS in-container | — | Path to the server certificate (PFX/PEM). |
| `ASPNETCORE_Kestrel__Certificates__Default__Password` | Only with the above | — | Certificate password (use a Secret). |

### 3.2 Authentication (OIDC / Keycloak) — section `AppSettings:Authentication`

| Variable | Required | Default (appsettings) | Description |
|---|---|---|---|
| `AppSettings__Authentication__Authority` | **Yes — hard-fails at startup if missing** | dev realm URL | OIDC issuer/authority. Must be **HTTPS** and reachable; OIDC metadata is fetched from it. |
| `AppSettings__Authentication__Audience` | **Yes** (unless validation disabled) | `ship-api` | Expected token audience (your API's client id in Keycloak). |
| `AppSettings__Authentication__AcceptAzpAsAudience` | No | `false` | Accept Keycloak `azp`/`resource_access` as the audience. |
| `AppSettings__Authentication__DisableAudienceValidation` | No | `false` | Disables audience validation entirely. Avoid in production. |

### 3.3 MongoDB — section `SourceDbSettings`

| Variable | Required | Default (appsettings) | Description |
|---|---|---|---|
| `SourceDbSettings__ConnectionString` | **Yes — hard-fails at startup if empty** | `mongodb://localhost:27017` | Mongo connection string (include credentials/authSource as needed). |
| `SourceDbSettings__DatabaseName` | **Yes** | `shipses` | Database name used for sync records and the `/health` ping. |

### 3.4 HMAC validation policy — section `AppSettings:Hmac`

These are **generic, non-secret** policy values (no client id or secret lives here).

| Variable | Required | Default | Description |
|---|---|---|---|
| `AppSettings__Hmac__Enabled` | No | `true` | Master switch for HMAC enforcement. If `false`, no client load and no signature checks. |
| `AppSettings__Hmac__RequireJwtAlso` | No | `true` | Require a valid JWT in addition to a valid signature. |
| `AppSettings__Hmac__SignatureHeader` | No | `X-SHIP-Signature` | Signature header name. |
| `AppSettings__Hmac__TimestampHeader` | No | `X-SHIP-Date` | Unix-seconds timestamp header. |
| `AppSettings__Hmac__NonceHeader` | No | `X-SHIP-Nonce` | Per-request nonce header (replay protection). |
| `AppSettings__Hmac__AllowedClockSkewSeconds` | No | `300` | Allowed timestamp drift and nonce-cache window. |
| `AppSettings__Hmac__HmacAlgo` | No | `HMACSHA256` | Signing algorithm (`HMACSHA256` or `HMACSHA512`). |
| `AppSettings__Hmac__BypassPaths__0` | No | `/health` | Paths exempt from HMAC (matched by path segments). Defaults to `/health` so probes work; add more as `__1`, `__2`, … |

### 3.5 Per-client HMAC secrets — section `AppSettings:Clients`

Clients are declared as an array under `AppSettings:Clients`. Each entry is loaded once at startup;
adding or rotating a client requires an application restart. Secret values must be supplied via
environment variables (or Kubernetes Secrets), not committed to `appsettings.json`. Repeat the block
for each client, incrementing the index (`__0__`, `__1__`, …).

| Variable | Required | Default | Description |
|---|---|---|---|
| `AppSettings__Clients__0__ClientId` | **Yes** (when HMAC enabled) | — | Must exactly match the caller's Keycloak `client_id`/`azp` claim. |
| `AppSettings__Clients__0__HmacSecret` | **Yes** (when HMAC enabled) | — | The key used to verify request signatures. **Secret.** Entries without it are skipped. |
| `AppSettings__Clients__0__ClientSecret` | No | — | The client's OAuth secret; carried for completeness, not used by HMAC validation. |
| `AppSettings__Clients__0__Status` | No | `ACTIVE` | `ACTIVE` (or absent) enables the client; `REVOKED` marks it revoked; any other value marks it inactive. Non-active clients get `403`. |

### 3.6 Rate limiting — section `AppSettings:RateLimiting`

| Variable | Required | Default | Description |
|---|---|---|---|
| `AppSettings__RateLimiting__Enabled` | No | `true` | Enables the global rate limiter. |
| `AppSettings__RateLimiting__PartitionBy` | No | `Ip` | `Ip` (per client IP) or `None` (single global bucket). |
| `AppSettings__RateLimiting__PermitLimit` | No | `20` | Requests allowed per window. |
| `AppSettings__RateLimiting__WindowSeconds` | No | `1` | Window length in seconds. |
| `AppSettings__RateLimiting__QueueLimit` | No | `100` | Queued requests when the window is full. |
| `AppSettings__RateLimiting__RejectionStatusCode` | No | `429` | Status returned when throttled. |

### 3.7 Logging (Serilog)

| Variable | Required | Default | Description |
|---|---|---|---|
| `Serilog__MinimumLevel__Default` | No | `Information` | Global log level. |
| `Logging__LogLevel__Default` | No | `Information` | Microsoft logging level. |

> Serilog writes JSON to **Console** and to a rolling file at **`logs/log.txt`** (relative to the app working dir). In a container, ensure `logs/` is writable, or mount a volume, or rely on the Console sink (recommended for k8s — collect stdout).

---

## 4. External dependency setup

### 4.1 MongoDB
Provision a database and a user with read/write. Supply the connection string and database name via §3.3.

### 4.2 OIDC / Keycloak
- Create (or reuse) a realm and an API client; set `AppSettings__Authentication__Authority` to the realm issuer URL and `Audience` to the API client id.
- Clients authenticate to Keycloak and present a JWT whose `client_id` (or `azp`) claim **must exactly match** the configured `ClientId` (see below).

### 4.3 Per-client HMAC secrets

The Ingestor reads all client credentials from configuration (`AppSettings:Clients`) **once at startup**. Each entry's `ClientId` must match the JWT `client_id`/`azp`, and its `HmacSecret` is the shared signing key.

**Supply each client via environment variables (or Kubernetes Secrets):**
```bash
AppSettings__Clients__0__ClientId=emr-a
AppSettings__Clients__0__HmacSecret=<long-random-high-entropy-key>
AppSettings__Clients__0__Status=ACTIVE
# add more clients by incrementing the index: __1__, __2__, …
```
The same `HmacSecret` value must also be given to that client so requests can be signed.

> **Adding/rotating a client requires an application restart** — the in-memory client set is built once at startup. There are no per-request lookups and no TTL refresh.

See `README.md` → "HMAC client credential resolution" for the request-signing contract.

---

## 5. Health endpoint

- **`GET /health`** — pings MongoDB. Returns `200 {status:"healthy"}`, `206` degraded, or `503` unhealthy. It does **not** check the IdP. It is **exempt from HMAC** (via `Hmac.BypassPaths`), so probes do not need to be signed/authenticated.
- API docs (non-prod aids): Swagger JSON is served; Scalar UI at `/` docs is enabled only in `Development`.

---

## 6. Startup logs to verify a good deployment

On boot the service logs a **configuration summary** plus the client load result. A healthy start looks like:

```
HMAC client load complete: 3 client(s) loaded from configuration.
Startup configuration summary:
  Environment             : Production
  Auth.Authority          : https://idp.example/realms/ship
  Auth.Audience           : ship-api (audience validation enabled)
  Mongo.DatabaseName      : shipses
  Mongo.ConnectionString  : set
  HMAC.Enabled            : True (algo HMACSHA256, requireJwtAlso True, clockSkew 300s)
  HMAC clients configured : 3
  HMAC clients loaded     : 3
  RateLimiting.Enabled    : True
SHIP SeS Ingestor API started and ready to accept requests
```

If **HMAC is enabled but 0 clients loaded**, the service logs a loud `ERROR`: check that `AppSettings:Clients` is populated (via appsettings or `AppSettings__Clients__<n>__ClientId` / `__HmacSecret` env vars) and that each entry has a non-empty `ClientId` and `HmacSecret`. `(NOT SET)` next to any field means that variable was not provided to the container.

---

## 7. Pre-deployment checklist

- [ ] `AppSettings__Authentication__Authority` set to the real IdP (HTTPS, reachable).
- [ ] `AppSettings__Authentication__Audience` matches the API client id.
- [ ] `SourceDbSettings__ConnectionString` + `__DatabaseName` point at the real Mongo.
- [ ] At least one client is configured under `AppSettings:Clients` with a non-empty `ClientId` and `HmacSecret` (per §4.3).
- [ ] Each client's `ClientId` equals the client's Keycloak `client_id`/`azp`.
- [ ] TLS terminated (ingress) or cert provided to Kestrel.

---

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| App exits at startup: *"Authority is required"* | `AppSettings__Authentication__Authority` missing | Set it (HTTPS issuer URL) |
| App exits: *"SourceDbSettings:ConnectionString is not configured"* | Mongo connection string empty | Set `SourceDbSettings__ConnectionString` |
| **Every** request `401`, even correctly signed | 0 HMAC clients loaded from configuration | Check the `HMAC client load` / `ERROR` lines: `AppSettings:Clients` populated, each entry has `ClientId` + `HmacSecret` |
| One client gets `401` "Unknown client" | JWT `client_id`/`azp` ≠ configured `ClientId` | Align the names |
| Requests `403` | Client `Status` is not `ACTIVE` (inactive/revoked) | Set `Status=ACTIVE` + restart |
| `400` "Missing X-SHIP-Date/Nonce" | Caller isn't signing requests | Configure the client to send the HMAC headers |
| `401` after a few minutes of clock drift | Timestamp outside `AllowedClockSkewSeconds` | Sync clocks / raise skew |
| `/health` returns `503` | Mongo unreachable | Check connection string / network |
| `429` responses | Rate limiter | Tune `AppSettings__RateLimiting__*` |
| Authenticated request `401` with valid token | IdP `Authority` unreachable or audience mismatch | Verify Authority reachability + `Audience` |
