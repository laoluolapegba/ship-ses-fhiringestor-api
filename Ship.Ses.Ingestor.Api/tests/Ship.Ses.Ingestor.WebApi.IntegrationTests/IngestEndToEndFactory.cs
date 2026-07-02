using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    /// <summary>
    /// End-to-end test host: real FhirIngestService -> real MongoSyncRepository -> real MongoDB.
    /// Only the external edges (the HMAC client is supplied via configuration, the IdP is stubbed) so a
    /// signed request can hit the database without standing up Vault or Keycloak. Mongo must actually be
    /// running and reachable.
    /// </summary>
    public sealed class IngestEndToEndFactory : WebApplicationFactory<Program>
    {
        public const string ClientId = "emr-a";
        public const string ClientSecret = "test-secret-a";
        public const string DatabaseName = "shipses";
        public const string ConnectionString = "mongodb://localhost:27017";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Host-scoped Mongo config so we never accidentally write to the dev "shipses" database.
            builder.UseSetting("SourceDbSettings:ConnectionString", ConnectionString);
            builder.UseSetting("SourceDbSettings:DatabaseName", DatabaseName);

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
                // Test authentication scheme supplies the client_id claim (no IdP needed).
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = E2ETestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = E2ETestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = E2ETestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, E2ETestAuthHandler>(E2ETestAuthHandler.SchemeName, _ => { });

                // NOTE: IFhirIngestService and IHealthService are NOT stubbed here.
                // The request flows through the real FhirIngestService -> real MongoSyncRepository
                // -> MongoDB.Driver -> the Mongo server.
            });
        }
    }

    internal sealed class E2ETestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        public E2ETestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "e2e-test"),
                new Claim("client_id", IngestEndToEndFactory.ClientId)
            };
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)),
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
