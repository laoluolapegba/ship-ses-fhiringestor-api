using Ship.Ses.Ingestor.Application.Models;

namespace Ship.Ses.Ingestor.Application.Interfaces
{
    public interface IClientCredentialResolver
    {
        Task<ClientCredential?> ResolveByClientIdAsync(string clientId, CancellationToken cancellationToken);
    }

    public interface IClientHmacCredentialRegistry
    {
        Task<ClientCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken);
    }
}
