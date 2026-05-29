# SHIP SeS FHIR Ingestor API

The **FHIR Ingestor API** is a lightweight, secure web API that allows Electronic Medical Record (EMR) systems to submit [FHIR](https://www.hl7.org/fhir/) compliant resources (e.g., `Patient`, `Encounter`) into the SHIP Edge Server (SES). These resources are stored in MongoDB and later processed and synchronized with the central Smart Health Information Platform (SHIP).

> **Deploying this service?** See **[DEPLOYMENT.md](DEPLOYMENT.md)** for the full environment-variable reference, dependency setup (MongoDB / Keycloak / Vault), run examples, startup-log checks, and troubleshooting.

---

##  Features

-  **FHIR-compliant JSON ingestion**
-  **JWT-based authentication** and client identity resolution
-  **Facility association** via client mapping
-  **MongoDB persistence** using structured `FhirSyncRecord` schema
-  **FHIR resource validation** via Firely SDK
-  **Audit logging and sync status tracking**
-  **Swagger UI & OpenAPI** support with versioning

---

## Repository Structure (Domain-Driven Design)
```
Ship.Ses.FhirIngest.Api/
├── Controllers/
│ └── FhirIngestController.cs
├── Services/
│ ├── IFhirResourceValidator.cs
│ ├── FirelyResourceValidator.cs
│ └── FacilityResolver.cs
├── Models/
│ ├── FhirSyncRecord.cs
│ ├── PatientSyncRecord.cs
│ └── FhirApiResponse.cs
├── Startup.cs / Program.cs
└── README.md
```

---


## 🔧 Configuration

App configuration is done via `appsettings.json` or environment variables.

### Example Configuration:
```json
"MongoDbSettings": {
  "ConnectionString": "mongodb://localhost:27017",
  "DatabaseName": "ship_ses",
  "CollectionName": "fhir_patient_queue"
},
"JwtSettings": {
  "Authority": "https://auth.myship.ng/",
  "Audience": "ship-ses"
}
**Authentication & Authorization**
The API expects an Authorization Bearer token (JWT) in all requests. The client_id is extracted from the token and used to associate the request with a registered SHIP facility.

Example header:

Authorization: Bearer <your-jwt-token>

### HMAC client credential resolution

HMAC validation uses the authenticated caller identity from the JWT, not the ingest request body. The ingestor reads the client identity from `client_id`, falling back to `azp` when `client_id` is absent.

Client-specific HMAC credentials are served from the in-memory client HMAC credential registry (`IClientHmacCredentialRegistry`), which is populated once at startup. SeS instance configuration must not contain client HMAC `ClientId` or `ClientSecret` values. `appsettings.json` may only hold generic HMAC validation settings such as header names, clock skew, enablement and algorithm policy.

Vault is the authoritative source for per-client HMAC secrets. At application startup, `VaultClientHmacCredentialLoader` lists every registered client under the configured prefix and loads each client's secret into memory. There are no per-request Vault calls; adding or rotating a client requires an application restart to reload. The default secret path pattern is:

```text
secret/data/emr-clients/{clientId}/hmac
```

The client set is discovered by listing the prefix (`secret/metadata/emr-clients` for KV v2). For example, `client_id=emr-a` resolves to `secret/data/emr-clients/emr-a/hmac`. The Vault secret should expose the HMAC secret in `clientSecret` by default, with optional `isActive`, `isRevoked`, `status`, and `allowedAlgorithms` metadata. The Vault token requires `read` capability on each client path and `list` capability on the prefix. Vault connection details are supplied through environment variables, not SeS appsettings:

```text
VAULT_ADDR=https://vault.example
VAULT_TOKEN=<vault token>
VAULT_HMAC_MOUNT=secret
VAULT_HMAC_KV_VERSION=2
VAULT_HMAC_PATH_TEMPLATE=emr-clients/{clientId}/hmac
VAULT_HMAC_SECRET_KEY=clientSecret
```

`FacilityId` remains required source facility metadata. It is not a security credential, is not used for HMAC credential lookup, and must not be used as a fallback client identity.

Tenant context is implied by the running SeS deployment/environment. The ingestor does not resolve tenant at runtime.

Example Request
Endpoint: POST /api/v1/fhir/ingest/Patient
{
  "resourceType": "Patient",
  "id": "123",
  "identifier": [{ "use": "official", "value": "ABC123" }],
  "name": [{ "text": "Jane Doe" }],
  "gender": "female",
  "birthDate": "1990-01-01"
}
Response:

{
  "status": "success",
  "message": "FHIR resource accepted for processing",
  "resourceType": "Patient",
  "resourceId": "123",
  "documentId": "666aa998a8d3f27fc41310ef"
}
**Running Locally**
dotnet build
dotnet run
Navigate to: https://localhost:{PORT}/swagger

**Testing & Validation**
Requests are validated for FHIR compliance using Firely SDK.

Invalid payloads return 400 Bad Request with error details.

Submissions are persisted with "status": "Pending" until synced.

**Technologies**
Component	Technology
Web Framework	ASP.NET Core Web API
Database	MongoDB
FHIR Validator	Firely .NET SDK
Auth	JWT / OAuth 2.0
Docs	Swagger / Swashbuckle
Logging	Serilog + Correlation ID
