using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Interfaces
{
    public interface IClientSyncConfigProvider
    {
        Task<bool> IsClientActiveAsync(string clientId);
        Task<IEnumerable<string>> GetEnabledResourcesAsync(string clientId);
        Task<bool> IsClientValidAsync(string clientId);
        Task<string?> GetFacilityIdAsync(string clientId);
    }
}