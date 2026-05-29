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
| **HashiCorp Vault** | Source of per-client HMAC signing secrets, loaded once at startup | Every signed request rejected with `401` |
| **OIDC IdP (Keycloak)** | JWT bearer validation | App fails to start (Authority missing) or all requests `401` |

---

## 2. How configuration is loaded

Configuration is layered; later sources override earlier ones:

1. **`appsettings.json`** — baked into the image. Holds **non-secret defaults only** (header names, clock skew, rate-limit policy, log config). It currently also contains **development** values for Authority/Audience/Mongo — these **must be overridden** in any real environment.
2. **`appsettings.{ASPNETCORE_ENVIRONMENT}.json`** — optional per-environment overrides.
3. **Environment variables** — the primary mechanism for deployment. Use these for everything environment-specific and **all secrets**.

### Two env-var naming conventions (important)

- **.NET configuration keys** use a double underscore `__` as the section separator.
  `AppSettings:Authentication:Authority` → `AppSettings__Authentication__Authority`
- **Vault connection settings** are read as **plain OS environment variables** (not `__`, not under `AppSettings`): `VAULT_ADDR`, `VAULT_TOKEN`, etc.

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
| `AppSettings__Hmac__Enabled` | No | `true` | Master switch for HMAC enforcement. If `false`, no Vault load and no signature checks. |
| `AppSettings__Hmac__RequireJwtAlso` | No | `true` | Require a valid JWT in addition to a valid signature. |
| `AppSettings__Hmac__SignatureHeader` | No | `X-SHIP-Signature` | Signature header name. |
| `AppSettings__Hmac__TimestampHeader` | No | `X-SHIP-Date` | Unix-seconds timestamp header. |
| `AppSettings__Hmac__NonceHeader` | No | `X-SHIP-Nonce` | Per-request nonce header (replay protection). |
| `AppSettings__Hmac__AllowedClockSkewSeconds` | No | `300` | Allowed timestamp drift and nonce-cache window. |
| `AppSettings__Hmac__HmacAlgo` | No | `HMACSHA256` | Signing algorithm (`HMACSHA256` or `HMACSHA512`). |
| `AppSettings__Hmac__BypassPaths__0` | No | `/health` | Paths exempt from HMAC (matched by path segments). Defaults to `/health` so probes work; add more as `__1`, `__2`, … |

### 3.5 Vault (per-client HMAC secrets) — plain OS env vars

| Variable | Required | Default | Description |
|---|---|---|---|
| `VAULT_ADDR` | **Yes** (when HMAC enabled) | — | Vault base URL, e.g. `https://vault.internal:8200`. |
| `VAULT_TOKEN` | **Yes** (when HMAC enabled) | — | Vault token. Needs `list` on the prefix and `read` on the client paths. **Secret.** |
| `VAULT_HMAC_MOUNT` | No | `secret` | KV mount point. |
| `VAULT_HMAC_KV_VERSION` | No | `2` | KV engine version (controls the `data`/`metadata` path segments). |
| `VAULT_HMAC_PATH_TEMPLATE` | No | `emr-clients/{clientId}/hmac` | **Logical** path template. Do **not** include `data`/`metadata` — the app inserts those for KV v2. |
| `VAULT_HMAC_SECRET_KEY` | No | `clientSecret` | Field inside the secret holding the HMAC key. |
| `VAULT_HMAC_STATUS_KEY` | No | `status` | Optional field; value `revoked`/`inactive` disables the client. |
| `VAULT_HMAC_IS_ACTIVE_KEY` | No | `isActive` | Optional boolean field. |
| `VAULT_HMAC_IS_REVOKED_KEY` | No | `isRevoked` | Optional boolean field. |

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
- Clients authenticate to Keycloak and present a JWT whose `client_id` (or `azp`) claim **must exactly match** the Vault client folder name (see below).

### 4.3 Vault — per-client HMAC secrets

The Ingestor reads all client secrets **once at startup**. The **path folder name is the ClientId** and must match the JWT `client_id`/`azp`. The HMAC key is stored in the `clientSecret` field.

**Store each client (KV v2):**
```bash
# CLI hides the "data" segment; this writes to secret/data/emr-clients/emr-a/hmac
vault kv put secret/emr-clients/emr-a/hmac \
    clientSecret="<long-random-high-entropy-key>" \
    isActive=true
```
The same `clientSecret` value is the shared signing key and must also be given to that client so requests can be signed.

**Policy the token needs:**
```hcl
# read each client's hmac secret
path "secret/data/emr-clients/*" {
  capabilities = ["read"]
}
# list the registered clients (startup discovery)
path "secret/metadata/emr-clients" {
  capabilities = ["list"]
}
```
```bash
vault policy write ses-ingestor-hmac ses-ingestor-hmac.hcl
```

> **Adding/rotating a client requires an application restart** — the in-memory client set is built once at startup. There are no per-request Vault calls and no TTL refresh.

See `README.md` → "HMAC client credential resolution" for the request-signing contract.

---

## 5. Health endpoint

- **`GET /health`** — pings MongoDB. Returns `200 {status:"healthy"}`, `206` degraded, or `503` unhealthy. It does **not** check Vault or the IdP. It is **exempt from HMAC** (via `Hmac.BypassPaths`), so probes do not need to be signed/authenticated.
- API docs (non-prod aids): Swagger JSON is served; Scalar UI at `/` docs is enabled only in `Development`.

---

## 6. Startup logs to verify a good deployment

On boot the service logs a **configuration summary** plus the Vault load result. A healthy start looks like:

```
Vault HMAC load: discovering clients at https://vault.internal:8200 (mount 'secret', KV v2, list prefix 'emr-clients'). ...
Vault HMAC credential load complete: 3 client(s) loaded into memory.
Startup configuration summary:
  Environment             : Production
  Auth.Authority          : https://idp.example/realms/ship
  Auth.Audience           : ship-api (audience validation enabled)
  Mongo.DatabaseName      : shipses
  Mongo.ConnectionString  : set
  HMAC.Enabled            : True (algo HMACSHA256, requireJwtAlso True, clockSkew 300s)
  HMAC clients loaded     : 3
  Vault.Addr              : https://vault.internal:8200
  Vault.Token             : set
  Vault.Mount/KV/Template : secret / v2 / emr-clients/{clientId}/hmac
  RateLimiting.Enabled    : True
SHIP SeS Ingestor API started and ready to accept requests
```

If **HMAC is enabled but 0 clients loaded**, the service logs a loud `ERROR` naming the three likely causes (token/reachability, missing `list`/`read` capability, no secrets under the prefix). `(NOT SET)` next to any field means that variable was not provided to the container.

---

## 7. Pre-deployment checklist

- [ ] `AppSettings__Authentication__Authority` set to the real IdP (HTTPS, reachable).
- [ ] `AppSettings__Authentication__Audience` matches the API client id.
- [ ] `SourceDbSettings__ConnectionString` + `__DatabaseName` point at the real Mongo.
- [ ] `VAULT_ADDR` + `VAULT_TOKEN` set; token has `list` + `read` per §4.3.
- [ ] At least one client secret exists in Vault under the prefix.
- [ ] Each Vault client folder name equals the client's Keycloak `client_id`/`azp`.
- [ ] TLS terminated (ingress) or cert provided to Kestrel.

---

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| App exits at startup: *"Authority is required"* | `AppSettings__Authentication__Authority` missing | Set it (HTTPS issuer URL) |
| App exits: *"SourceDbSettings:ConnectionString is not configured"* | Mongo connection string empty | Set `SourceDbSettings__ConnectionString` |
| **Every** request `401`, even correctly signed | 0 HMAC clients loaded from Vault | Check the `Vault HMAC load` / `ERROR` lines: token, reachability, `list`/`read` capability, secrets exist |
| One client gets `401` "Unknown client" | JWT `client_id`/`azp` ≠ Vault folder name | Align the names |
| Requests `403` | Client marked `isActive:false` / `isRevoked:true` in Vault | Update the Vault secret + restart |
| `400` "Missing X-SHIP-Date/Nonce" | Caller isn't signing requests | Configure the client to send the HMAC headers |
| `401` after a few minutes of clock drift | Timestamp outside `AllowedClockSkewSeconds` | Sync clocks / raise skew |
| `/health` returns `503` | Mongo unreachable | Check connection string / network |
| `429` responses | Rate limiter | Tune `AppSettings__RateLimiting__*` |
| Authenticated request `401` with valid token | IdP `Authority` unreachable or audience mismatch | Verify Authority reachability + `Audience` |
