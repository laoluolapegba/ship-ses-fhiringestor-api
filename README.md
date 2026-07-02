# SHIP SeS FHIR Ingestor API

The **FHIR Ingestor API** is a lightweight, secure web API that allows Electronic Medical Record (EMR) systems to submit [FHIR](https://www.hl7.org/fhir/) compliant resources (e.g., `Patient`, `Encounter`) into the SHIP Edge Server (SES). These resources are stored in MongoDB and later processed and synchronized with the central Smart Health Information Platform (SHIP).

> **Deploying this service?** See **[DEPLOYMENT.md](DEPLOYMENT.md)** for the full environment-variable reference, dependency setup (MongoDB / Keycloak), run examples, startup-log checks, and troubleshooting.

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

Client-specific HMAC credentials are served from the in-memory client HMAC credential registry (`IClientHmacCredentialRegistry`), which is populated once at startup.

Configuration is the authoritative source for per-client HMAC secrets. At application startup, `ConfigurationClientHmacCredentialLoader` reads the `AppSettings:Clients` array and loads each client's secret into memory. There are no per-request lookups; adding or rotating a client requires an application restart to reload. Each client is declared as:

```json
"AppSettings": {
  "Clients": [
    {
      "ClientId": "ses-client-a",
      "ClientSecret": "<oauth-client-secret>",
      "HmacSecret": "<hmac-signing-secret>",
      "Status": "ACTIVE"
    }
  ]
}
```

`HmacSecret` is the key used to verify request signatures; `ClientSecret` is the client's OAuth secret and is carried for completeness but not used by HMAC validation. `Status` of `ACTIVE` (or absent) enables the client; any other value marks it inactive, and `REVOKED` marks it revoked (both yield `403`). The `ClientId` must exactly match the caller's Keycloak `client_id`/`azp` claim.

Secret values are not committed to `appsettings.json`; in each deployment they are supplied through environment variables using the standard double-underscore convention (no Vault address or token is required):

```text
AppSettings__Clients__0__ClientId=ses-client-a
AppSettings__Clients__0__HmacSecret=<hmac-signing-secret>
AppSettings__Clients__0__Status=ACTIVE
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
