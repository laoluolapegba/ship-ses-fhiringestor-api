using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Domain
{
    public class HealthResult
    {
        public HealthStatus Status { get; set; }
        public string? Reason { get; set; }

        public static HealthResult Healthy() => new() { Status = HealthStatus.Healthy };
        public static HealthResult Degraded(string reason) => new() { Status = HealthStatus.Degraded, Reason = reason };
        public static HealthResult Unhealthy(string reason) => new() { Status = HealthStatus.Unhealthy, Reason = reason };
    }

    public enum HealthStatus
    {
        Unhealthy = 0,
        Degraded = 1,
        Healthy = 2
    }
}
