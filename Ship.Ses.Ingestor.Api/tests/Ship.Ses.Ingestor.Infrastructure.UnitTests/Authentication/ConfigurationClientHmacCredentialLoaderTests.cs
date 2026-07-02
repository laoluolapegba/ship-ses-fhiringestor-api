using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Infrastructure.Authentication;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Authentication
{
    public class ConfigurationClientHmacCredentialLoaderTests
    {
        [Fact]
        public void Loads_each_configured_client_mapping_hmac_secret_to_signing_key()
        {
            var loader = CreateLoader(
                new HmacClientDefinition { ClientId = "emr-a", ClientSecret = "oauth-a", HmacSecret = "hmac-a", Status = "ACTIVE" },
                new HmacClientDefinition { ClientId = "emr-b", ClientSecret = "oauth-b", HmacSecret = "hmac-b", Status = "ACTIVE" });

            var clients = loader.LoadAll();

            Assert.Equal(2, clients.Count);
            // The middleware signs with ClientCredential.ClientSecret, which must carry the HmacSecret.
            Assert.Equal("hmac-a", clients["emr-a"].ClientSecret);
            Assert.Equal("hmac-b", clients["emr-b"].ClientSecret);
            Assert.True(clients["emr-a"].IsActive);
            Assert.False(clients["emr-a"].IsRevoked);
            Assert.Equal("AppSettings:Clients/emr-a", clients["emr-a"].SecretReference);
        }

        [Fact]
        public void Missing_status_defaults_to_active()
        {
            var loader = CreateLoader(
                new HmacClientDefinition { ClientId = "emr-a", HmacSecret = "hmac-a" });

            var clients = loader.LoadAll();

            Assert.True(clients["emr-a"].IsActive);
            Assert.False(clients["emr-a"].IsRevoked);
        }

        [Fact]
        public void Non_active_status_marks_client_inactive()
        {
            var loader = CreateLoader(
                new HmacClientDefinition { ClientId = "emr-a", HmacSecret = "hmac-a", Status = "INACTIVE" });

            var clients = loader.LoadAll();

            Assert.False(clients["emr-a"].IsActive);
        }

        [Fact]
        public void Revoked_status_marks_client_revoked()
        {
            var loader = CreateLoader(
                new HmacClientDefinition { ClientId = "emr-a", HmacSecret = "hmac-a", Status = "REVOKED" });

            var clients = loader.LoadAll();

            Assert.True(clients["emr-a"].IsRevoked);
            Assert.False(clients["emr-a"].IsActive);
        }

        [Fact]
        public void Skips_entries_without_client_id_or_hmac_secret()
        {
            var loader = CreateLoader(
                new HmacClientDefinition { ClientId = "", HmacSecret = "hmac-x" },
                new HmacClientDefinition { ClientId = "emr-no-secret", HmacSecret = "" },
                new HmacClientDefinition { ClientId = "emr-a", HmacSecret = "hmac-a" });

            var clients = loader.LoadAll();

            Assert.Single(clients);
            Assert.True(clients.ContainsKey("emr-a"));
        }

        [Fact]
        public void Returns_empty_when_no_clients_configured()
        {
            var loader = CreateLoader();

            var clients = loader.LoadAll();

            Assert.Empty(clients);
        }

        private static ConfigurationClientHmacCredentialLoader CreateLoader(params HmacClientDefinition[] clients)
        {
            var options = Options.Create(new HmacClientRegistryOptions { Clients = clients.ToList() });
            return new ConfigurationClientHmacCredentialLoader(options, NullLogger<ConfigurationClientHmacCredentialLoader>.Instance);
        }
    }
}
