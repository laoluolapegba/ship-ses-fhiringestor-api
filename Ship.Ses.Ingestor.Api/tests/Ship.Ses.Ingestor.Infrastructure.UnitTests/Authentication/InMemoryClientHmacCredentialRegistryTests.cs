using Ship.Ses.Ingestor.Application.Models;
using Ship.Ses.Ingestor.Infrastructure.Authentication;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Authentication
{
    public class InMemoryClientHmacCredentialRegistryTests
    {
        [Fact]
        public async Task Returns_credential_for_known_client_after_initialization()
        {
            var registry = new InMemoryClientHmacCredentialRegistry();
            registry.Initialize(new Dictionary<string, ClientCredential>
            {
                ["emr-a"] = new() { ClientId = "emr-a", ClientSecret = "secret-a" }
            });

            var credential = await registry.GetByClientIdAsync("emr-a", CancellationToken.None);

            Assert.NotNull(credential);
            Assert.Equal("emr-a", credential.ClientId);
            Assert.Equal("secret-a", credential.ClientSecret);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task Returns_null_for_unknown_client()
        {
            var registry = new InMemoryClientHmacCredentialRegistry();
            registry.Initialize(new Dictionary<string, ClientCredential>
            {
                ["emr-a"] = new() { ClientId = "emr-a", ClientSecret = "secret-a" }
            });

            var credential = await registry.GetByClientIdAsync("emr-unknown", CancellationToken.None);

            Assert.Null(credential);
        }

        [Fact]
        public async Task Returns_null_before_initialization()
        {
            var registry = new InMemoryClientHmacCredentialRegistry();

            var credential = await registry.GetByClientIdAsync("emr-a", CancellationToken.None);

            Assert.Null(credential);
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public async Task Initialization_replaces_previous_client_set()
        {
            var registry = new InMemoryClientHmacCredentialRegistry();
            registry.Initialize(new Dictionary<string, ClientCredential>
            {
                ["emr-a"] = new() { ClientId = "emr-a", ClientSecret = "secret-a" }
            });
            registry.Initialize(new Dictionary<string, ClientCredential>
            {
                ["emr-b"] = new() { ClientId = "emr-b", ClientSecret = "secret-b" }
            });

            Assert.Null(await registry.GetByClientIdAsync("emr-a", CancellationToken.None));
            Assert.NotNull(await registry.GetByClientIdAsync("emr-b", CancellationToken.None));
        }
    }
}
