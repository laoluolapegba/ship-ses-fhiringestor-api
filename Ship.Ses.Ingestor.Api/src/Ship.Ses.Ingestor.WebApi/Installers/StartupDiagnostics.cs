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
    /// No secret values (Vault token, Mongo connection string) are written to the log.
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

            var vaultAddr = Environment.GetEnvironmentVariable("VAULT_ADDR");
            var vaultTokenSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_TOKEN"));
            var vaultMount = Environment.GetEnvironmentVariable("VAULT_HMAC_MOUNT") ?? "secret";
            var vaultKv = Environment.GetEnvironmentVariable("VAULT_HMAC_KV_VERSION") ?? "2";
            var vaultTemplate = Environment.GetEnvironmentVariable("VAULT_HMAC_PATH_TEMPLATE") ?? "emr-clients/{clientId}/hmac";
            var vaultPrefix = ListPrefixOf(vaultTemplate);

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
                "  HMAC clients loaded     : {ClientCount}\n" +
                "  Vault.Addr              : {VaultAddr}\n" +
                "  Vault.Token             : {VaultToken}\n" +
                "  Vault.Mount/KV/Template : {Mount} / v{Kv} / {Template}\n" +
                "  RateLimiting.Enabled    : {RateLimiting}",
                app.Environment.EnvironmentName,
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)",
                Display(authority),
                Display(audience), disableAud ? "DISABLED" : "enabled",
                Display(mongoDb),
                string.IsNullOrWhiteSpace(mongoConn) ? "(NOT SET)" : "set",
                hmac.Enabled, hmac.HmacAlgo, hmac.RequireJwtAlso, hmac.AllowedClockSkewSeconds,
                clientCount,
                Display(vaultAddr),
                vaultTokenSet ? "set" : "(NOT SET)",
                vaultMount, vaultKv, vaultTemplate,
                rlEnabled);

            if (hmac.Enabled && clientCount == 0)
            {
                log.LogError(
                    "HMAC is ENABLED but 0 clients were loaded from Vault — every signed request will be rejected with 401. " +
                    "Check, in order: (1) VAULT_ADDR and VAULT_TOKEN are set and Vault is reachable; " +
                    "(2) the Vault token has 'list' capability on {Mount}/metadata/{Prefix} and 'read' on {Mount}/data/{Prefix}/*; " +
                    "(3) at least one client secret exists under that prefix. See the 'Vault HMAC load' log lines above for the exact target and failure reason.",
                    vaultMount, vaultPrefix, vaultMount, vaultPrefix);
            }

            return app;
        }

        private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(NOT SET)" : value;

        private static string ListPrefixOf(string template)
        {
            var idx = template.IndexOf("{clientId}", StringComparison.Ordinal);
            var prefix = idx >= 0 ? template[..idx] : template;
            return prefix.Trim('/');
        }
    }
}
