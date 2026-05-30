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
