using Ship.Ses.Ingestor.Domain.Sync;
using Ship.Ses.Ingestor.Domain;
using System.Collections.Generic;

namespace Ship.Ses.Ingestor.Infrastructure.Persistance.Configuration
{
    public interface ISyncMetricsCollector
    {
        Task<SyncClientStatus> CollectStatusAsync(string clientId);
        Task<IEnumerable<SyncClientMetric>> CollectMetricsAsync(string clientId);

        //IEnumerable<SyncClientMetric> CollectMetrics(string clientId);
    }
}