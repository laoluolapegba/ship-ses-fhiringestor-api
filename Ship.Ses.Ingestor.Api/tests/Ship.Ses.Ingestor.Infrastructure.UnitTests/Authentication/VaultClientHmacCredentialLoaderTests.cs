using Microsoft.Extensions.Logging.Abstractions;
using Ship.Ses.Ingestor.Infrastructure.Authentication;
using System.Net;
using System.Text;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Authentication
{
    public class VaultClientHmacCredentialLoaderTests : IDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment = new();

        public VaultClientHmacCredentialLoaderTests()
        {
            Capture("VAULT_ADDR");
            Capture("VAULT_TOKEN");
            Capture("VAULT_HMAC_MOUNT");
            Capture("VAULT_HMAC_PATH_TEMPLATE");
            Capture("VAULT_HMAC_KV_VERSION");
            Capture("VAULT_HMAC_SECRET_KEY");
            Capture("VAULT_HMAC_STATUS_KEY");
            Capture("VAULT_HMAC_IS_ACTIVE_KEY");
            Capture("VAULT_HMAC_IS_REVOKED_KEY");
        }

        [Fact]
        public async Task Looks_up_hmac_secret_from_expected_vault_path()
        {
            SetVaultEnvironment();
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                    {
                      "data": {
                        "data": {
                          "clientSecret": "secret-a",
                          "isActive": true,
                          "isRevoked": false,
                          "allowedAlgorithms": ["HMACSHA256"]
                        }
                      }
                    }
                    """)
            });
            var loader = CreateLoader(handler);

            var credential = await loader.GetByClientIdAsync("emr-a", CancellationToken.None);

            Assert.NotNull(credential);
            Assert.Equal("emr-a", credential.ClientId);
            Assert.Equal("secret-a", credential.ClientSecret);
            Assert.True(credential.IsActive);
            Assert.False(credential.IsRevoked);
            Assert.Equal("secret/emr-clients/emr-a/hmac", credential.SecretReference);
            Assert.Equal("https://vault.example/v1/secret/data/emr-clients/emr-a/hmac", handler.Requests.Single().RequestUri!.ToString());
            Assert.Equal("vault-token", handler.Requests.Single().Headers.GetValues("X-Vault-Token").Single());
        }

        [Fact]
        public async Task Missing_vault_secret_returns_null()
        {
            SetVaultEnvironment();
            var loader = CreateLoader(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

            var credential = await loader.GetByClientIdAsync("emr-missing", CancellationToken.None);

            Assert.Null(credential);
        }

        [Fact]
        public async Task Unconfigured_vault_returns_null_without_request()
        {
            Environment.SetEnvironmentVariable("VAULT_ADDR", null);
            Environment.SetEnvironmentVariable("VAULT_TOKEN", null);
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var loader = CreateLoader(handler);

            var credential = await loader.GetByClientIdAsync("emr-a", CancellationToken.None);

            Assert.Null(credential);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Supports_kv_v1_path_when_configured()
        {
            SetVaultEnvironment();
            Environment.SetEnvironmentVariable("VAULT_HMAC_KV_VERSION", "1");
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                    {
                      "data": {
                        "clientSecret": "secret-a"
                      }
                    }
                    """)
            });
            var loader = CreateLoader(handler);

            var credential = await loader.GetByClientIdAsync("emr-a", CancellationToken.None);

            Assert.NotNull(credential);
            Assert.Equal("https://vault.example/v1/secret/emr-clients/emr-a/hmac", handler.Requests.Single().RequestUri!.ToString());
        }

        [Fact]
        public async Task LoadAll_lists_clients_then_loads_each_secret()
        {
            SetVaultEnvironment();
            var handler = new CapturingHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;

                if (path == "/v1/secret/metadata/emr-clients")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent("""
                            { "data": { "keys": ["emr-a/", "emr-b"] } }
                            """)
                    };
                }

                var secret = path.Contains("emr-a") ? "secret-a" : "secret-b";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent($$"""
                        { "data": { "data": { "clientSecret": "{{secret}}", "isActive": true } } }
                        """)
                };
            });
            var loader = CreateLoader(handler);

            var clients = await loader.LoadAllAsync(CancellationToken.None);

            Assert.Equal(2, clients.Count);
            Assert.Equal("secret-a", clients["emr-a"].ClientSecret);
            Assert.Equal("secret-b", clients["emr-b"].ClientSecret);
            Assert.Equal("https://vault.example/v1/secret/metadata/emr-clients?list=true", handler.Requests.First().RequestUri!.ToString());
            Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/v1/secret/data/emr-clients/emr-a/hmac");
            Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/v1/secret/data/emr-clients/emr-b/hmac");
        }

        [Fact]
        public async Task LoadAll_skips_clients_whose_secret_is_missing()
        {
            SetVaultEnvironment();
            var handler = new CapturingHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;

                if (path == "/v1/secret/metadata/emr-clients")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent("""
                            { "data": { "keys": ["emr-a", "emr-gone"] } }
                            """)
                    };
                }

                if (path.Contains("emr-gone"))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent("""
                        { "data": { "data": { "clientSecret": "secret-a" } } }
                        """)
                };
            });
            var loader = CreateLoader(handler);

            var clients = await loader.LoadAllAsync(CancellationToken.None);

            Assert.Single(clients);
            Assert.True(clients.ContainsKey("emr-a"));
            Assert.False(clients.ContainsKey("emr-gone"));
        }

        [Fact]
        public async Task LoadAll_returns_empty_when_unconfigured_without_request()
        {
            Environment.SetEnvironmentVariable("VAULT_ADDR", null);
            Environment.SetEnvironmentVariable("VAULT_TOKEN", null);
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var loader = CreateLoader(handler);

            var clients = await loader.LoadAllAsync(CancellationToken.None);

            Assert.Empty(clients);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task LoadAll_returns_empty_when_prefix_not_found()
        {
            SetVaultEnvironment();
            var loader = CreateLoader(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

            var clients = await loader.LoadAllAsync(CancellationToken.None);

            Assert.Empty(clients);
        }

        public void Dispose()
        {
            foreach (var pair in _originalEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        private void Capture(string name)
        {
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        }

        private static void SetVaultEnvironment()
        {
            Environment.SetEnvironmentVariable("VAULT_ADDR", "https://vault.example");
            Environment.SetEnvironmentVariable("VAULT_TOKEN", "vault-token");
            Environment.SetEnvironmentVariable("VAULT_HMAC_MOUNT", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_PATH_TEMPLATE", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_KV_VERSION", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_SECRET_KEY", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_STATUS_KEY", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_IS_ACTIVE_KEY", null);
            Environment.SetEnvironmentVariable("VAULT_HMAC_IS_REVOKED_KEY", null);
        }

        private static VaultClientHmacCredentialLoader CreateLoader(HttpMessageHandler handler)
            => new(new HttpClient(handler), NullLogger<VaultClientHmacCredentialLoader>.Instance);

        private static StringContent JsonContent(string json)
            => new(json, Encoding.UTF8, "application/json");

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public List<HttpRequestMessage> Requests { get; } = new();

            public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_handler(request));
            }
        }
    }
}
