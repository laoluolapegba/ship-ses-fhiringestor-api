using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    /// <summary>
    /// Posts a properly-signed request through the real pipeline and asserts the document
    /// actually landed in MongoDB. Requires a reachable Mongo at IngestEndToEndFactory.ConnectionString.
    /// </summary>
    public class IngestEndToEndTests : IClassFixture<IngestEndToEndFactory>
    {
        private const string IngestPath = "/api/v1/fhir-ingest";

        private readonly IngestEndToEndFactory _factory;
        private readonly ITestOutputHelper _out;

        public IngestEndToEndTests(IngestEndToEndFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _out = output;
        }

        [Fact]
        public async Task Signed_request_persists_a_record_visible_in_mongodb()
        {
            // Fail fast with a clear message if Mongo is unreachable.
            await AssertMongoIsReachable();

            var correlationId = $"e2e-{Guid.NewGuid():N}";
            var facilityId = "fac-e2e";
            var shipService = "PDS";
            var body = $$"""
                {
                  "shipService": "{{shipService}}",
                  "resourceId": "p-e2e-1",
                  "facilityId": "{{facilityId}}",
                  "correlationId": "{{correlationId}}",
                  "callbackUrl": "https://callback.example/status",
                  "fhirJson": { "resourceType": "Patient", "id": "p-e2e-1" }
                }
                """;

            var client = _factory.CreateClient();
            var request = BuildSignedRequest(body, IngestEndToEndFactory.ClientId, IngestEndToEndFactory.ClientSecret);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            _out.WriteLine($"POST {IngestPath} -> {(int)response.StatusCode} {response.StatusCode}");

            // Read back from MongoDB using the unique (clientId, facilityId, correlationId) tuple.
            var mongo = new MongoClient(IngestEndToEndFactory.ConnectionString);
            var collection = mongo.GetDatabase(IngestEndToEndFactory.DatabaseName)
                                  .GetCollection<BsonDocument>("transformed_pool_patients");

            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("clientId", IngestEndToEndFactory.ClientId),
                Builders<BsonDocument>.Filter.Eq("facilityId", facilityId),
                Builders<BsonDocument>.Filter.Eq("correlationId", correlationId));

            var persisted = await collection.Find(filter).FirstOrDefaultAsync();
            Assert.NotNull(persisted);

            _out.WriteLine("--- persisted MongoDB document ---");
            _out.WriteLine(persisted.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true }));

            // Spot-check the new metadata fields:
            Assert.Equal(shipService, persisted["targetSystem"].AsString);
            Assert.Equal(1, persisted["schemaVersion"].AsInt32);
            Assert.Equal("R4", persisted["fhirVersion"].AsString);
            Assert.Equal(shipService, persisted["shipService"].AsString);
            Assert.Equal("Patient", persisted["resourceType"].AsString);
        }

        [Fact]
        public async Task Complex_patient_payload_persists_with_emr_callback_url()
        {
            // Fail fast with a clear message if Mongo is unreachable.
            await AssertMongoIsReachable();

            var correlationId = $"e2e-complex-{Guid.NewGuid():N}";
            var facilityId = "FAC-LUTH-001";
            var shipService = "PDS";
            const string callbackUrl = "http://localhost:3000/api/event";

            // A richer, realistic FHIR R4 Patient: multiple identifiers, names, telecom,
            // address, marital status, contact, communication, GP/org references and extensions.
            var body = $$"""
                {
                  "shipService": "{{shipService}}",
                  "resourceId": "patient-9f3c-2025",
                  "facilityId": "{{facilityId}}",
                  "correlationId": "{{correlationId}}",
                  "callbackUrl": "{{callbackUrl}}",
                  "fhirJson": {
                    "resourceType": "Patient",
                    "id": "patient-9f3c-2025",
                    "meta": {
                      "versionId": "1",
                      "lastUpdated": "2025-11-14T09:30:00+01:00",
                      "source": "emr-luth",
                      "profile": [ "http://ship.gov.ng/fhir/StructureDefinition/ship-patient" ]
                    },
                    "text": {
                      "status": "generated",
                      "div": "<div xmlns=\"http://www.w3.org/1999/xhtml\">Adaeze Ngozi Okafor, F, 1989-04-12</div>"
                    },
                    "extension": [
                      {
                        "url": "http://ship.gov.ng/fhir/StructureDefinition/patient-tribe",
                        "valueString": "Igbo"
                      },
                      {
                        "url": "http://hl7.org/fhir/StructureDefinition/patient-birthPlace",
                        "valueAddress": {
                          "city": "Enugu",
                          "state": "Enugu",
                          "country": "NG"
                        }
                      }
                    ],
                    "identifier": [
                      {
                        "use": "official",
                        "system": "http://ship.gov.ng/nin",
                        "value": "12345678901",
                        "type": {
                          "coding": [
                            { "system": "http://terminology.hl7.org/CodeSystem/v2-0203", "code": "NI", "display": "National identifier" }
                          ]
                        }
                      },
                      {
                        "use": "secondary",
                        "system": "http://luth.org/mrn",
                        "value": "MRN-0098213",
                        "assigner": { "display": "Lagos University Teaching Hospital" }
                      }
                    ],
                    "active": true,
                    "name": [
                      {
                        "use": "official",
                        "family": "Okafor",
                        "given": [ "Adaeze", "Ngozi" ],
                        "prefix": [ "Mrs" ]
                      },
                      {
                        "use": "maiden",
                        "family": "Eze",
                        "given": [ "Adaeze" ]
                      }
                    ],
                    "telecom": [
                      { "system": "phone", "value": "+2348031234567", "use": "mobile", "rank": 1 },
                      { "system": "email", "value": "adaeze.okafor@example.com", "use": "home" }
                    ],
                    "gender": "female",
                    "birthDate": "1989-04-12",
                    "deceasedBoolean": false,
                    "address": [
                      {
                        "use": "home",
                        "type": "physical",
                        "line": [ "14 Bourdillon Road", "Ikoyi" ],
                        "city": "Lagos",
                        "district": "Eti-Osa",
                        "state": "Lagos",
                        "postalCode": "101233",
                        "country": "NG"
                      }
                    ],
                    "maritalStatus": {
                      "coding": [
                        { "system": "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "code": "M", "display": "Married" }
                      ]
                    },
                    "multipleBirthInteger": 2,
                    "contact": [
                      {
                        "relationship": [
                          {
                            "coding": [
                              { "system": "http://terminology.hl7.org/CodeSystem/v2-0131", "code": "C", "display": "Emergency Contact" }
                            ]
                          }
                        ],
                        "name": { "family": "Okafor", "given": [ "Chukwuemeka" ] },
                        "telecom": [ { "system": "phone", "value": "+2348059876543", "use": "mobile" } ],
                        "gender": "male"
                      }
                    ],
                    "communication": [
                      {
                        "language": {
                          "coding": [ { "system": "urn:ietf:bcp:47", "code": "ig", "display": "Igbo" } ]
                        },
                        "preferred": true
                      },
                      {
                        "language": {
                          "coding": [ { "system": "urn:ietf:bcp:47", "code": "en", "display": "English" } ]
                        },
                        "preferred": false
                      }
                    ],
                    "generalPractitioner": [ { "reference": "Practitioner/dr-emeka-123", "display": "Dr. Emeka Obi" } ],
                    "managingOrganization": { "reference": "Organization/luth", "display": "Lagos University Teaching Hospital" }
                  }
                }
                """;

            var client = _factory.CreateClient();
            var request = BuildSignedRequest(body, IngestEndToEndFactory.ClientId, IngestEndToEndFactory.ClientSecret);

            var response = await client.SendAsync(request);
            _out.WriteLine($"POST {IngestPath} -> {(int)response.StatusCode} {response.StatusCode}");
            _out.WriteLine($"correlationId = {correlationId}");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            // Read back from MongoDB using the unique (clientId, facilityId, correlationId) tuple.
            var mongo = new MongoClient(IngestEndToEndFactory.ConnectionString);
            var collection = mongo.GetDatabase(IngestEndToEndFactory.DatabaseName)
                                  .GetCollection<BsonDocument>("transformed_pool_patients");

            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("clientId", IngestEndToEndFactory.ClientId),
                Builders<BsonDocument>.Filter.Eq("facilityId", facilityId),
                Builders<BsonDocument>.Filter.Eq("correlationId", correlationId));

            var persisted = await collection.Find(filter).FirstOrDefaultAsync();
            Assert.NotNull(persisted);

            _out.WriteLine("--- persisted MongoDB document (shipses.transformed_pool_patients) ---");
            _out.WriteLine(persisted.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true }));

            // The EMR callback URL the transmitter will notify on completion.
            Assert.Equal(callbackUrl, persisted["clientEMRCallbackUrl"].AsString);
            // Persistence metadata + identity fields.
            Assert.Equal(shipService, persisted["targetSystem"].AsString);
            Assert.Equal(shipService, persisted["shipService"].AsString);
            Assert.Equal(1, persisted["schemaVersion"].AsInt32);
            Assert.Equal("R4", persisted["fhirVersion"].AsString);
            Assert.Equal("Patient", persisted["resourceType"].AsString);
            Assert.Equal("Pending", persisted["status"].AsString);
            Assert.Equal("API", persisted["extractSource"].AsString);
            // The nested FHIR body round-tripped intact.
            Assert.Equal("Okafor", persisted["fhirJson"]["name"][0]["family"].AsString);
        }

        private static async Task AssertMongoIsReachable()
        {
            try
            {
                var client = new MongoClient(IngestEndToEndFactory.ConnectionString);
                var admin = client.GetDatabase("admin");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"MongoDB is not reachable at {IngestEndToEndFactory.ConnectionString}. " +
                    "Start one (e.g. `docker run -d --name ses-mongo-test -p 27017:27017 mongo:7`) and retry.",
                    ex);
            }
        }

        private static HttpRequestMessage BuildSignedRequest(string body, string clientId, string secret)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = Guid.NewGuid().ToString("N");
            var bodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            var stringToSign = string.Join("\n", "POST", IngestPath, "", bodyHash, timestamp.ToString(), nonce, clientId);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            var request = new HttpRequestMessage(HttpMethod.Post, IngestPath)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-SHIP-Date", timestamp.ToString());
            request.Headers.Add("X-SHIP-Nonce", nonce);
            request.Headers.Add("X-SHIP-Signature", $"kid={clientId};alg=HMACSHA256;sig={signature}");
            return request;
        }
    }
}
