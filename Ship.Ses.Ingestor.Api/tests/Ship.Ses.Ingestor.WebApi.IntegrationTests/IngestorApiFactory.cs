using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Patients;
using Ship.Ses.Ingestor.Application.Shared;
using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Patients;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    /// <summary>
    /// Boots the real application pipeline in-process and replaces only the external edges:
    /// the HMAC client is supplied through configuration (so the startup loader populates a known
    /// client), authentication uses a test scheme that supplies the client_id claim, and Mongo-backed
    /// services are faked. Everything else — middleware order, HMAC validation, the /health bypass — is real.
    /// </summary>
    public sealed class IngestorApiFactory : WebApplicationFactory<Program>
    {
        public const string ClientId = "emr-a";
        public const string ClientSecret = "test-secret-a";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Supply one known HMAC client via configuration so the startup loader populates it.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppSettings:Clients:0:ClientId"] = ClientId,
                    ["AppSettings:Clients:0:HmacSecret"] = ClientSecret,
                    ["AppSettings:Clients:0:Status"] = "ACTIVE"
                });
            });

            builder.ConfigureTestServices(services =>
            {
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
}
