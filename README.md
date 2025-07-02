# SHIP SeS FHIR Ingestor API

The **FHIR Ingestor API** is a lightweight, secure web API that allows Electronic Medical Record (EMR) systems to submit [FHIR](https://www.hl7.org/fhir/) compliant resources (e.g., `Patient`, `Encounter`) into the SHIP Edge Server (SES). These resources are stored in MongoDB and later processed and synchronized with the central Smart Health Information Platform (SHIP).

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
