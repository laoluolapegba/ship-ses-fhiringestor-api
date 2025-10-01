using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.DTOs
{
    public sealed class PatientTransmissionStatusQueryResponse
    {
        public string? CorrelationId { get; set; }

        public string TransactionId { get; set; } = default!;
        public string ShipId { get; set; } = default!;
        public string Status { get; set; } = default!;                 
        public string Message { get; set; } = default!;
        public string ResourceType { get; set; } = "Patient";
        public string? ResourceId { get; set; }
        public DateTime ReceivedAtUtc { get; set; }

        // (EMR callback delivery)
        public string CallbackStatus { get; set; } = "Pending";        
        public int CallbackAttempts { get; set; }
        public DateTime? CallbackNextAttemptAt { get; set; }
        public DateTime? CallbackDeliveredAt { get; set; }
        public string? EmrTargetUrl { get; set; }
        public int? EmrResponseStatusCode { get; set; }
        public string? EmrResponseBody { get; set; }

        // include the full FHIR Patient resource (off by default)
        public System.Text.Json.Nodes.JsonObject? Data { get; set; }
    }

}
