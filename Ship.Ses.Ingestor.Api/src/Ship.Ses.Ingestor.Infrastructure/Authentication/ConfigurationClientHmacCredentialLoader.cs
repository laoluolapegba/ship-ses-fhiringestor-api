using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Models;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    /// <summary>
    /// Loads per-client HMAC credentials from application configuration (the <c>AppSettings:Clients</c>
    /// array) at startup. Configuration is the sole source of truth: values are supplied through
    /// appsettings or environment variables, with no calls to Vault. Each client's <c>HmacSecret</c>
    /// becomes the signing key checked by the middleware; adding or rotating a client requires a restart.
    /// </summary>
    public sealed class ConfigurationClientHmacCredentialLoader
    {
        private const string ActiveStatus = "ACTIVE";
        private const string RevokedStatus = "REVOKED";

        private readonly IReadOnlyList<HmacClientDefinition> _clients;
        private readonly ILogger<ConfigurationClientHmacCredentialLoader> _logger;

        public ConfigurationClientHmacCredentialLoader(
            IOptions<HmacClientRegistryOptions> options,
            ILogger<ConfigurationClientHmacCredentialLoader> logger)
        {
            _clients = options.Value.Clients;
            _logger = logger;
        }

        /// <summary>
        /// Maps every configured client into an in-memory dictionary keyed by ClientId. Entries missing
        /// a ClientId or HmacSecret are skipped; secret values are never logged.
        /// </summary>
        public IReadOnlyDictionary<string, ClientCredential> LoadAll()
        {
            var result = new Dictionary<string, ClientCredential>(StringComparer.Ordinal);

            if (_clients.Count == 0)
            {
                _logger.LogWarning("HMAC client load skipped: no clients configured under 'AppSettings:Clients'. No clients were loaded, so every signed request will be rejected with 401.");
                return result;
            }

            foreach (var client in _clients)
            {
                if (string.IsNullOrWhiteSpace(client.ClientId))
                {
                    _logger.LogWarning("HMAC client load skipped a configured entry: ClientId is missing.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(client.HmacSecret))
                {
                    _logger.LogWarning("HMAC client load skipped client {ClientId}: HmacSecret is not configured.", client.ClientId);
                    continue;
                }

                var status = client.Status?.Trim();
                var isRevoked = string.Equals(status, RevokedStatus, StringComparison.OrdinalIgnoreCase);
                var isActive = string.IsNullOrWhiteSpace(status)
                    || string.Equals(status, ActiveStatus, StringComparison.OrdinalIgnoreCase);

                result[client.ClientId] = new ClientCredential
                {
                    ClientId = client.ClientId,
                    ClientSecret = client.HmacSecret,
                    SecretReference = $"AppSettings:Clients/{client.ClientId}",
                    IsActive = isActive,
                    IsRevoked = isRevoked
                };
            }

            _logger.LogInformation("HMAC client load complete: {Count} client(s) loaded from configuration.", result.Count);
            return result;
        }
    }
}
