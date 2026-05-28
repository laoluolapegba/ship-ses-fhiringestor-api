using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Application.Models;
using Ship.Ses.Ingestor.Infrastructure.Authentication;
using Ship.Ses.Ingestor.Infrastructure.Settings;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Authentication
{
    public class HmacAuthMiddlewareTests
    {
        [Fact]
        public async Task Missing_client_claim_is_rejected()
        {
            var ctx = CreateContext(BuildBody("facility-a"), clientId: null);
            Sign(ctx, "client-a", "secret-a");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a")
            });

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Unknown_client_id_is_rejected()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "unknown-client");
            Sign(ctx, "unknown-client", "secret-a");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>());

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Inactive_client_id_is_rejected()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a");
            Sign(ctx, "client-a", "secret-a");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a") with { IsActive = false }
            });

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Revoked_client_id_is_rejected()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a");
            Sign(ctx, "client-a", "secret-a");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a") with { IsRevoked = true }
            });

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Valid_client_id_with_valid_signature_is_accepted()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a");
            Sign(ctx, "client-a", "secret-a");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a")
            });
            var nextCalled = false;

            await middleware.InvokeAsync(ctx, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            Assert.True(nextCalled);
        }

        [Fact]
        public async Task Client_id_claim_is_used_when_present()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a", azp: "client-b");
            Sign(ctx, "client-a", "secret-a");
            var registry = new RecordingRegistry(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a"),
                ["client-b"] = Active("client-b", "secret-b")
            });
            var middleware = CreateMiddleware(registry);

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal("client-a", registry.RequestedClientIds.Single());
        }

        [Fact]
        public async Task Azp_claim_is_used_when_client_id_is_absent()
        {
            var ctx = CreateContext(BuildBody("facility-a"), clientId: null, azp: "client-b");
            Sign(ctx, "client-b", "secret-b");
            var registry = new RecordingRegistry(new Dictionary<string, ClientCredential>
            {
                ["client-b"] = Active("client-b", "secret-b")
            });
            var middleware = CreateMiddleware(registry);
            var nextCalled = false;

            await middleware.InvokeAsync(ctx, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            Assert.True(nextCalled);
            Assert.Equal("client-b", registry.RequestedClientIds.Single());
        }

        [Fact]
        public async Task Valid_client_id_with_invalid_signature_is_rejected()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a");
            Sign(ctx, "client-a", "wrong-secret");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a")
            });

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Facility_id_is_not_used_for_credential_resolution()
        {
            var ctx = CreateContext(BuildBody("client-b"), "client-a");
            Sign(ctx, "client-a", "secret-a");
            var registry = new RecordingRegistry(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a"),
                ["client-b"] = Active("client-b", "secret-b")
            });
            var middleware = CreateMiddleware(registry);
            var nextCalled = false;

            await middleware.InvokeAsync(ctx, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            Assert.True(nextCalled);
            Assert.Equal("client-a", registry.RequestedClientIds.Single());
        }

        [Fact]
        public async Task Payload_client_id_is_not_required_and_not_used_for_credential_resolution()
        {
            var ctx = CreateContext(BuildBody("facility-a", payloadClientId: "client-b"), "client-a");
            Sign(ctx, "client-a", "secret-a");
            var registry = new RecordingRegistry(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a"),
                ["client-b"] = Active("client-b", "secret-b")
            });
            var middleware = CreateMiddleware(registry);
            var nextCalled = false;

            await middleware.InvokeAsync(ctx, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            Assert.True(nextCalled);
            Assert.Equal("client-a", registry.RequestedClientIds.Single());
        }

        [Fact]
        public async Task Signature_kid_must_match_authenticated_client()
        {
            var ctx = CreateContext(BuildBody("facility-a"), "client-a");
            Sign(ctx, "client-b", "secret-b");
            var middleware = CreateMiddleware(new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a"),
                ["client-b"] = Active("client-b", "secret-b")
            });

            await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

            Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Multiple_clients_use_their_own_secrets()
        {
            var credentials = new Dictionary<string, ClientCredential>
            {
                ["client-a"] = Active("client-a", "secret-a"),
                ["client-b"] = Active("client-b", "secret-b")
            };
            var clientA = CreateContext(BuildBody("facility-a"), "client-a");
            var clientB = CreateContext(BuildBody("facility-b"), "client-b");
            Sign(clientA, "client-a", "secret-a", nonce: "nonce-a");
            Sign(clientB, "client-b", "secret-b", nonce: "nonce-b");
            var middleware = CreateMiddleware(credentials);
            var accepted = 0;

            await middleware.InvokeAsync(clientA, _ =>
            {
                accepted++;
                return Task.CompletedTask;
            });
            await middleware.InvokeAsync(clientB, _ =>
            {
                accepted++;
                return Task.CompletedTask;
            });

            Assert.Equal(2, accepted);
        }

        [Fact]
        public void Hmac_settings_do_not_expose_static_client_credentials()
        {
            var propertyNames = typeof(HmacAuthSettings)
                .GetProperties()
                .Select(p => p.Name)
                .ToArray();

            Assert.DoesNotContain("Clients", propertyNames);
            Assert.DoesNotContain("ClientId", propertyNames);
            Assert.DoesNotContain("ClientSecret", propertyNames);
        }

        private static HmacAuthMiddleware CreateMiddleware(IReadOnlyDictionary<string, ClientCredential> credentials)
            => CreateMiddleware(new RecordingRegistry(credentials));

        private static HmacAuthMiddleware CreateMiddleware(IClientHmacCredentialRegistry registry)
            => new(
                Options.Create(new HmacAuthSettings
                {
                    Enabled = true,
                    RequireJwtAlso = false,
                    AllowedClockSkewSeconds = 300,
                    HmacAlgo = "HMACSHA256"
                }),
                new ClientCredentialResolver(registry),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<HmacAuthMiddleware>.Instance);

        private static DefaultHttpContext CreateContext(string body, string? clientId = "client-a", string? azp = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/v1/fhir-ingest";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            ctx.Response.Body = new MemoryStream();
            var claims = new List<Claim> { new("sub", "tester") };
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                claims.Add(new Claim("client_id", clientId));
            }
            if (!string.IsNullOrWhiteSpace(azp))
            {
                claims.Add(new Claim("azp", azp));
            }

            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return ctx;
        }

        private static string BuildBody(string facilityId, string? payloadClientId = null)
        {
            var payloadClientIdProperty = payloadClientId is null ? string.Empty : $@"""clientId"":""{payloadClientId}"",";
            return $$"""
                {
                  {{payloadClientIdProperty}}
                  "shipService":"PDS",
                  "resourceId":"patient-1",
                  "facilityId":"{{facilityId}}",
                  "callbackUrl":"https://callback.example/status",
                  "correlationId":"corr-1",
                  "fhirJson":{"resourceType":"Patient","id":"patient-1"}
                }
                """;
        }

        private static void Sign(DefaultHttpContext ctx, string clientId, string secret, string nonce = "nonce-1")
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bodyBytes = ((MemoryStream)ctx.Request.Body).ToArray();
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToBase64String(sha.ComputeHash(bodyBytes));
            var stringToSign = string.Join("\n", "POST", "/api/v1/fhir-ingest", "", bodyHash, timestamp.ToString(), nonce, clientId);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            ctx.Request.Headers["X-SHIP-Date"] = timestamp.ToString();
            ctx.Request.Headers["X-SHIP-Nonce"] = nonce;
            ctx.Request.Headers["X-SHIP-Signature"] = $"kid={clientId};alg=HMACSHA256;sig={signature}";
            ctx.Request.Body.Position = 0;
        }

        private static ClientCredential Active(string clientId, string secret)
            => new() { ClientId = clientId, ClientSecret = secret, IsActive = true };

        private sealed class RecordingRegistry : IClientHmacCredentialRegistry
        {
            private readonly IReadOnlyDictionary<string, ClientCredential> _credentials;
            public List<string> RequestedClientIds { get; } = new();

            public RecordingRegistry(IReadOnlyDictionary<string, ClientCredential> credentials)
            {
                _credentials = credentials;
            }

            public Task<ClientCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken)
            {
                RequestedClientIds.Add(clientId);
                return Task.FromResult(_credentials.TryGetValue(clientId, out var credential) ? credential : null);
            }
        }
    }
}
