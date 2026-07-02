using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Infrastructure.Authentication;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.WebApi.Installers
{
    /// <summary>
    /// Emits a single, redacted summary of the effective runtime configuration at startup, plus a
    /// loud error when a misconfiguration would silently reject every request. Intended to give the
    /// team deploying this service enough to diagnose a first deployment from the logs alone.
    /// No secret values (client/HMAC secrets, Mongo connection string) are written to the log.
    /// </summary>
    public static class StartupDiagnostics
    {
        public static WebApplication LogEffectiveConfiguration(this WebApplication app)
        {
            var log = app.Logger;
            var cfg = app.Configuration;

            var authority = cfg["AppSettings:Authentication:Authority"];
            var audience = cfg["AppSettings:Authentication:Audience"];
            var disableAud = cfg.GetValue("AppSettings:Authentication:DisableAudienceValidation", false);

            var mongoConn = cfg["SourceDbSettings:ConnectionString"];
            var mongoDb = cfg["SourceDbSettings:DatabaseName"];

            var hmac = app.Services.GetRequiredService<IOptions<HmacAuthSettings>>().Value;
            var clientCount = app.Services.GetService<InMemoryClientHmacCredentialRegistry>()?.Count ?? 0;
            var configuredClientCount = cfg.GetSection("AppSettings:Clients").GetChildren().Count();

            var rlEnabled = cfg.GetValue("AppSettings:RateLimiting:Enabled", true);

            log.LogInformation(
                "Startup configuration summary:\n" +
                "  Environment             : {Env}\n" +
                "  Listening URLs          : {Urls}\n" +
                "  Auth.Authority          : {Authority}\n" +
                "  Auth.Audience           : {Audience} (audience validation {AudState})\n" +
                "  Mongo.DatabaseName      : {MongoDb}\n" +
                "  Mongo.ConnectionString  : {MongoConn}\n" +
                "  HMAC.Enabled            : {HmacEnabled} (algo {Algo}, requireJwtAlso {ReqJwt}, clockSkew {Skew}s)\n" +
                "  HMAC clients configured : {ConfiguredClientCount}\n" +
                "  HMAC clients loaded     : {ClientCount}\n" +
                "  RateLimiting.Enabled    : {RateLimiting}",
                app.Environment.EnvironmentName,
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)",
                Display(authority),
                Display(audience), disableAud ? "DISABLED" : "enabled",
                Display(mongoDb),
                string.IsNullOrWhiteSpace(mongoConn) ? "(NOT SET)" : "set",
                hmac.Enabled, hmac.HmacAlgo, hmac.RequireJwtAlso, hmac.AllowedClockSkewSeconds,
                configuredClientCount,
                clientCount,
                rlEnabled);

            if (hmac.Enabled && clientCount == 0)
            {
                log.LogError(
                    "HMAC is ENABLED but 0 clients were loaded from configuration — every signed request will be rejected with 401. " +
                    "Check, in order: (1) the 'AppSettings:Clients' array is populated (via appsettings or " +
                    "'AppSettings__Clients__<n>__ClientId' / '__HmacSecret' environment variables); " +
                    "(2) each entry has a non-empty ClientId and HmacSecret. See the 'HMAC client load' log lines above for skipped entries.");
            }

            return app;
        }

        private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(NOT SET)" : value;
    }
}
