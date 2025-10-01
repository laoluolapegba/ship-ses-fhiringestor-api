using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Sync;
using System.Collections.Generic;

namespace Ship.Ses.Ingestor.Application.Sync
{
    public interface ISyncMetricsCollector
    {
        Task<SyncClientStatus> CollectStatusAsync(string clientId);
        Task<IEnumerable<SyncClientMetric>> CollectMetricsAsync(string clientId);

        //IEnumerable<SyncClientMetric> CollectMetrics(string clientId);
    }
}