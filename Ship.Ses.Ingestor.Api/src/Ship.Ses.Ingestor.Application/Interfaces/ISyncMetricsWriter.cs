using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Sync;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Sync
{
    public interface ISyncMetricsWriter
    {
        Task WriteStatusAsync(SyncClientStatus status);
        Task WriteMetricAsync(SyncClientMetric metric);
        Task WriteMetricsAsync(IEnumerable<SyncClientMetric> metrics);
    }
}