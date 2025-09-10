using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Domain.SyncModels
{
    public sealed record RateLimitingSettings
    {
        public bool Enabled { get; init; } = true;
        public string PolicyName { get; init; } = "ingest"; 
        public string Strategy { get; init; } = "FixedWindow"; 
        public string PartitionBy { get; init; } = "Ip";     // "Ip" or "None" (global)
        public int PermitLimit { get; init; } = 20;
        public int WindowSeconds { get; init; } = 1;
        public int QueueLimit { get; init; } = 100;
        public string QueueProcessingOrder { get; init; } = "OldestFirst"; // or "NewestFirst"
        public int RejectionStatusCode { get; init; } = 429;
    }

}
