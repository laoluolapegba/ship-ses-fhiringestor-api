using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Patients;
using Ship.Ses.Ingestor.Application.Shared;
using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Infrastructure.Authentication;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    /// <summary>
    /// Boots the real application pipeline in-process and replaces only the external edges:
    /// Vault HTTP is stubbed (so the startup loader populates a known client), authentication
    /// uses a test scheme that supplies the client_id claim, and Mongo-backed services are faked.
    /// Everything else — middleware order, HMAC validation, the /health bypass — is the real thing.
    /// </summary>
    public sealed class IngestorApiFactory : WebApplicationFactory<Program>
    {
        public const string ClientId = "emr-a";
        public const string ClientSecret = "test-secret-a";

        public IngestorApiFactory()
        {
            Environment.SetEnvironmentVariable("VAULT_ADDR", "http://vault.test");
            Environment.SetEnvironmentVariable("VAULT_TOKEN", "test-token");
            Environment.SetEnvironmentVariable("VAULT_HMAC_MOUNT", "secret");
            Environment.SetEnvironmentVariable("VAULT_HMAC_KV_VERSION", "2");
            Environment.SetEnvironmentVariable("VAULT_HMAC_PATH_TEMPLATE", "emr-clients/{clientId}/hmac");
            Environment.SetEnvironmentVariable("VAULT_HMAC_SECRET_KEY", "clientSecret");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.ConfigureTestServices(services =>
            {
                // Serve canned Vault responses so the startup loader populates one known client.
                services.AddHttpClient<VaultClientHmacCredentialLoader>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeVaultHandler(ClientId, ClientSecret));

                // Replace JWT bearer with a test scheme that authenticates and supplies client_id.
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                // Stub the Mongo-backed services so the test needs no database.
                services.RemoveAll<IFhirIngestService>();
                services.AddScoped<IFhirIngestService, FakeFhirIngestService>();
                services.RemoveAll<IHealthService>();
                services.AddScoped<IHealthService, FakeHealthService>();
            });
        }
    }

    internal sealed class FakeFhirIngestService : IFhirIngestService
    {
        public Task<IdempotentInsertResult<FhirSyncRecord>> IngestAsyncReturningExisting(FhirIngestRequest request, string clientId)
            => Task.FromResult(new IdempotentInsertResult<FhirSyncRecord> { Outcome = IdempotentInsertOutcome.Inserted });
    }

    internal sealed class FakeHealthService : IHealthService
    {
        public Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(HealthResult.Healthy());
    }

    internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "integration-test"),
                new Claim("client_id", IngestorApiFactory.ClientId)
            };
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)),
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    internal sealed class FakeVaultHandler : HttpMessageHandler
    {
        private readonly string _clientId;
        private readonly string _secret;

        public FakeVaultHandler(string clientId, string secret)
        {
            _clientId = clientId;
            _secret = secret;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.Contains("/metadata/")
                ? $$"""{ "data": { "keys": ["{{_clientId}}"] } }"""
                : $$"""{ "data": { "data": { "clientSecret": "{{_secret}}", "isActive": true } } }""";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
