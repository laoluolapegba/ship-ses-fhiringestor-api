using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    public class FhirIngestEndpointTests : IClassFixture<IngestorApiFactory>
    {
        private const string IngestPath = "/api/v1/fhir-ingest";

        private const string Body = """
            {
              "shipService": "PDS",
              "resourceId": "p1",
              "facilityId": "fac-1",
              "correlationId": "corr-1",
              "callbackUrl": "https://callback.example/status",
              "fhirJson": { "resourceType": "Patient", "id": "p1" }
            }
            """;

        private readonly IngestorApiFactory _factory;

        public FhirIngestEndpointTests(IngestorApiFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Correctly_signed_request_is_accepted()
        {
            var client = _factory.CreateClient();
            var request = BuildSignedRequest(Body, IngestorApiFactory.ClientId, IngestorApiFactory.ClientSecret);

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        [Fact]
        public async Task Authenticated_but_unsigned_request_is_unauthorized()
        {
            var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, IngestPath)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Request_signed_with_wrong_secret_is_unauthorized()
        {
            var client = _factory.CreateClient();
            var request = BuildSignedRequest(Body, IngestorApiFactory.ClientId, "wrong-secret");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Health_endpoint_is_reachable_without_signing()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
