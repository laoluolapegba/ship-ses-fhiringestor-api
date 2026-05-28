using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Application.Models;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    public sealed class ClientCredentialResolver : IClientCredentialResolver
    {
        private readonly IClientHmacCredentialRegistry _registry;

        public ClientCredentialResolver(IClientHmacCredentialRegistry registry)
        {
            _registry = registry;
        }

        public Task<ClientCredential?> ResolveByClientIdAsync(string clientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Task.FromResult<ClientCredential?>(null);
            }

            return _registry.GetByClientIdAsync(clientId, cancellationToken);
        }
    }
}
