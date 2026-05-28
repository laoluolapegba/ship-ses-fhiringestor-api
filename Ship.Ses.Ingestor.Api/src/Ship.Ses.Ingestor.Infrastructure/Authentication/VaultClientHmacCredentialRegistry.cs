using Microsoft.Extensions.Logging;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Application.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    public sealed class VaultClientHmacCredentialRegistry : IClientHmacCredentialRegistry
    {
        private const string DefaultMount = "secret";
        private const string DefaultPathTemplate = "ses/clients/{clientId}/hmac";
        private const string DefaultSecretKey = "clientSecret";
        private const string DefaultStatusKey = "status";
        private const string DefaultIsActiveKey = "isActive";
        private const string DefaultIsRevokedKey = "isRevoked";
        private readonly HttpClient _httpClient;
        private readonly ILogger<VaultClientHmacCredentialRegistry> _logger;

        public VaultClientHmacCredentialRegistry(HttpClient httpClient, ILogger<VaultClientHmacCredentialRegistry> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

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
                var baseAddress = settings.Address!.TrimEnd('/') + "/";
                if (_httpClient.BaseAddress is null || !string.Equals(_httpClient.BaseAddress.ToString(), baseAddress, StringComparison.Ordinal))
                {
                    _httpClient.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
                }

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

        private static string BuildLogicalPath(VaultHmacSecretSettings settings, string clientId)
        {
            var relativePath = settings.PathTemplate!.Replace("{clientId}", Uri.EscapeDataString(clientId), StringComparison.Ordinal);
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
