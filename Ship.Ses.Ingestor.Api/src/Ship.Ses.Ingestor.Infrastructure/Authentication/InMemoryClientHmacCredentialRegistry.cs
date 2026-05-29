using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Application.Models;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    /// <summary>
    /// Holds the per-client HMAC credentials loaded once from Vault at application startup.
    /// Lookups are served entirely from memory; there are no per-request Vault calls.
    /// Adding or rotating a client requires reloading via a restart.
    /// </summary>
    public sealed class InMemoryClientHmacCredentialRegistry : IClientHmacCredentialRegistry
    {
        private volatile IReadOnlyDictionary<string, ClientCredential> _clients =
            new Dictionary<string, ClientCredential>(StringComparer.Ordinal);

        public int Count => _clients.Count;

        /// <summary>
        /// Replaces the in-memory client set. Called once during startup after the Vault load completes.
        /// </summary>
        public void Initialize(IReadOnlyDictionary<string, ClientCredential> clients)
        {
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        }

        public Task<ClientCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Task.FromResult<ClientCredential?>(null);
            }

            return Task.FromResult(_clients.TryGetValue(clientId, out var credential) ? credential : null);
        }
    }
}
