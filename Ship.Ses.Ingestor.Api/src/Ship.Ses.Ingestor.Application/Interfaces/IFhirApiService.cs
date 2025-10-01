using Ship.Ses.Ingestor.Domain.SyncModels;
using Ship.Ses.Ingestor.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Infrastructure.Persistance.Configuration
{
    public interface IFhirApiService
    {
        public Task<FhirApiResponse> SendAsync(
        FhirOperation operation,
        string resourceType,
        string resourceId = null,
        string jsonPayload = null,
        string? callbackUrl = null,
        CancellationToken cancellationToken = default);
    }
}
