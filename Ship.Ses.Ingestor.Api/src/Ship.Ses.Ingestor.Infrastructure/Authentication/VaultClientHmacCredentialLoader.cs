using Microsoft.Extensions.Logging;
using Ship.Ses.Ingestor.Application.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    /// <summary>
    /// Reads per-client HMAC credentials from Vault at application startup.
    /// Vault is the sole source of truth: the set of registered clients is discovered
    /// by listing the configured prefix, and each client's secret is fetched once.
    /// There are no per-request Vault calls; adding or rotating a client requires a restart.
    /// </summary>
    public sealed class VaultClientHmacCredentialLoader
    {
        private const string DefaultMount = "secret";
        private const string DefaultPathTemplate = "emr-clients/{clientId}/hmac";
        private const string DefaultSecretKey = "clientSecret";
        private const string DefaultStatusKey = "status";
        private const string DefaultIsActiveKey = "isActive";
        private const string DefaultIsRevokedKey = "isRevoked";
        private const string ClientIdPlaceholder = "{clientId}";
        private readonly HttpClient _httpClient;
        private readonly ILogger<VaultClientHmacCredentialLoader> _logger;

        public VaultClientHmacCredentialLoader(HttpClient httpClient, ILogger<VaultClientHmacCredentialLoader> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Discovers every registered client under the configured Vault prefix and loads each
        /// client's HMAC credential into an in-memory dictionary keyed by ClientId.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, ClientCredential>> LoadAllAsync(CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, ClientCredential>(StringComparer.Ordinal);

            var settings = VaultHmacSecretSettings.FromEnvironment();
            if (!settings.IsConfigured)
            {
                _logger.LogWarning("Vault HMAC credential load skipped: VAULT_ADDR and/or VAULT_TOKEN are not set. No clients were loaded, so every signed request will be rejected with 401.");
                return result;
            }

            _logger.LogInformation("Vault HMAC load: discovering clients at {VaultAddress} (mount '{Mount}', KV v{KvVersion}, list prefix '{Prefix}'). Token is read from VAULT_TOKEN and never logged.",
                settings.Address, settings.Mount, settings.KvVersion, BuildListPrefix(settings));

            var clientIds = await ListClientIdsAsync(settings, cancellationToken);
            if (clientIds.Count == 0)
            {
                _logger.LogWarning("Vault HMAC credential load found no registered clients under prefix '{Prefix}'.", BuildListPrefix(settings));
                return result;
            }

            foreach (var clientId in clientIds)
            {
                var credential = await GetByClientIdAsync(clientId, cancellationToken);
                if (credential is null)
                {
                    _logger.LogWarning("Vault HMAC credential load skipped client {ClientId}: secret could not be read.", clientId);
                    continue;
                }

                result[credential.ClientId] = credential;
            }

            _logger.LogInformation("Vault HMAC credential load complete: {Count} client(s) loaded into memory.", result.Count);
            return result;
        }

        /// <summary>
        /// Reads a single client's HMAC credential from Vault. Used as the per-client fetch
        /// primitive during the startup load.
        /// </summary>
        public async Task<ClientCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return null;
            }

            var settings = VaultHmacSecretSettings.FromEnvironment();
            if (!settings.IsConfigured)
            {
                _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: Vault address or token is not configured.", clientId);
                return null;
            }

            var logicalPath = BuildLogicalPath(settings, clientId);
            var requestPath = BuildVaultApiPath(settings, logicalPath);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
            request.Headers.Add("X-Vault-Token", settings.Token);

            HttpResponseMessage response;
            try
            {
                EnsureBaseAddress(settings);
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogWarning(ex, "Vault HMAC credential lookup failed for ClientId {ClientId}: Vault request failed.", clientId);
                return null;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: secret path was not found.", clientId);
                    return null;
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: Vault access denied.", clientId);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: Vault returned status {StatusCode}.",
                        clientId, (int)response.StatusCode);
                    return null;
                }

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                var secretData = GetSecretData(payload, settings.KvVersion);
                if (secretData.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: secret data is empty.", clientId);
                    return null;
                }

                var secret = ReadString(secretData, settings.SecretKey);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    _logger.LogWarning("Vault HMAC credential lookup failed for ClientId {ClientId}: HMAC secret field is unavailable.", clientId);
                    return null;
                }

                var isRevoked = ReadBool(secretData, settings.IsRevokedKey) ?? IsStatus(secretData, settings.StatusKey, "revoked");
                var isActive = ReadBool(secretData, settings.IsActiveKey) ?? !IsStatus(secretData, settings.StatusKey, "inactive");

                return new ClientCredential
                {
                    ClientId = clientId,
                    ClientSecret = secret,
                    SecretReference = logicalPath,
                    IsActive = isActive,
                    IsRevoked = isRevoked,
                    AllowedAlgorithms = ReadStringArray(secretData, "allowedAlgorithms")
                };
            }
        }

        private async Task<IReadOnlyList<string>> ListClientIdsAsync(VaultHmacSecretSettings settings, CancellationToken cancellationToken)
        {
            var requestPath = BuildListApiPath(settings);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{requestPath}?list=true");
            request.Headers.Add("X-Vault-Token", settings.Token);

            HttpResponseMessage response;
            try
            {
                EnsureBaseAddress(settings);
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogWarning(ex, "Vault HMAC client list failed: Vault request failed for prefix '{Prefix}'.", BuildListPrefix(settings));
                return Array.Empty<string>();
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Vault HMAC client list failed: prefix '{Prefix}' was not found.", BuildListPrefix(settings));
                    return Array.Empty<string>();
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Vault HMAC client list failed: Vault access denied for prefix '{Prefix}'. The token requires 'list' capability.",
                        BuildListPrefix(settings));
                    return Array.Empty<string>();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Vault HMAC client list failed: Vault returned status {StatusCode} for prefix '{Prefix}'.",
                        (int)response.StatusCode, BuildListPrefix(settings));
                    return Array.Empty<string>();
                }

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                return ReadListKeys(payload);
            }
        }

        private void EnsureBaseAddress(VaultHmacSecretSettings settings)
        {
            var baseAddress = settings.Address!.TrimEnd('/') + "/";
            if (_httpClient.BaseAddress is null || !string.Equals(_httpClient.BaseAddress.ToString(), baseAddress, StringComparison.Ordinal))
            {
                _httpClient.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
            }
        }

        private static string BuildLogicalPath(VaultHmacSecretSettings settings, string clientId)
        {
            var relativePath = settings.PathTemplate!.Replace(ClientIdPlaceholder, Uri.EscapeDataString(clientId), StringComparison.Ordinal);
            return $"{settings.Mount!.Trim('/')}/{relativePath.Trim('/')}";
        }

        private static string BuildVaultApiPath(VaultHmacSecretSettings settings, string logicalPath)
        {
            var mount = settings.Mount!.Trim('/');
            var relativePath = logicalPath[(mount.Length + 1)..];

            return settings.KvVersion == 2
                ? $"v1/{mount}/data/{relativePath}"
                : $"v1/{logicalPath}";
        }

        private static string BuildListApiPath(VaultHmacSecretSettings settings)
        {
            var mount = settings.Mount!.Trim('/');
            var prefix = BuildListPrefix(settings);

            return settings.KvVersion == 2
                ? $"v1/{mount}/metadata/{prefix}"
                : $"v1/{mount}/{prefix}";
        }

        private static string BuildListPrefix(VaultHmacSecretSettings settings)
        {
            var template = settings.PathTemplate!;
            var placeholderIndex = template.IndexOf(ClientIdPlaceholder, StringComparison.Ordinal);
            var prefix = placeholderIndex >= 0 ? template[..placeholderIndex] : template;
            return prefix.Trim('/');
        }

        private static IReadOnlyList<string> ReadListKeys(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return keys.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.TrimEnd('/'))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static JsonElement GetSecretData(JsonElement payload, int kvVersion)
        {
            if (!payload.TryGetProperty("data", out var data))
            {
                return default;
            }

            if (kvVersion == 2 && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nestedData))
            {
                return nestedData;
            }

            return data;
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }

        private static bool? ReadBool(JsonElement root, string propertyName)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }

                if (property.Value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(property.Value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static bool IsStatus(JsonElement root, string propertyName, string expected)
        {
            var status = ReadString(root, propertyName);
            return string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyCollection<string> ReadStringArray(JsonElement root, string propertyName)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                return property.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray();
            }

            return Array.Empty<string>();
        }

        private sealed record VaultHmacSecretSettings
        {
            public string? Address { get; init; }
            public string? Token { get; init; }
            public string? Mount { get; init; }
            public string? PathTemplate { get; init; }
            public int KvVersion { get; init; }
            public string SecretKey { get; init; } = DefaultSecretKey;
            public string StatusKey { get; init; } = DefaultStatusKey;
            public string IsActiveKey { get; init; } = DefaultIsActiveKey;
            public string IsRevokedKey { get; init; } = DefaultIsRevokedKey;

            public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(Token);

            public static VaultHmacSecretSettings FromEnvironment()
            {
                return new VaultHmacSecretSettings
                {
                    Address = Environment.GetEnvironmentVariable("VAULT_ADDR"),
                    Token = Environment.GetEnvironmentVariable("VAULT_TOKEN"),
                    Mount = Environment.GetEnvironmentVariable("VAULT_HMAC_MOUNT") ?? DefaultMount,
                    PathTemplate = Environment.GetEnvironmentVariable("VAULT_HMAC_PATH_TEMPLATE") ?? DefaultPathTemplate,
                    KvVersion = int.TryParse(Environment.GetEnvironmentVariable("VAULT_HMAC_KV_VERSION"), out var kvVersion)
                        ? kvVersion
                        : 2,
                    SecretKey = Environment.GetEnvironmentVariable("VAULT_HMAC_SECRET_KEY") ?? DefaultSecretKey,
                    StatusKey = Environment.GetEnvironmentVariable("VAULT_HMAC_STATUS_KEY") ?? DefaultStatusKey,
                    IsActiveKey = Environment.GetEnvironmentVariable("VAULT_HMAC_IS_ACTIVE_KEY") ?? DefaultIsActiveKey,
                    IsRevokedKey = Environment.GetEnvironmentVariable("VAULT_HMAC_IS_REVOKED_KEY") ?? DefaultIsRevokedKey
                };
            }
        }
    }
}
