using Ship.Ses.Ingestor.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Shared
{
    public interface IHealthService
    {
        //Task<HealthResult> CheckHealthAsync();
        Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);

        
    }

}
