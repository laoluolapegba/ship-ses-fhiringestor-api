using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Application.DTOs
{
    

    public sealed class PatientTransmissionStatusResponse
    {
        public string? CorrelationId { get; set; }
        public string TransactionId { get; set; } = default!;
        public string StatusRecorded { get; set; } = default!;
        public bool Duplicate { get; set; }
        public DateTimeOffset RecordedAt { get; set; }
    }
}
