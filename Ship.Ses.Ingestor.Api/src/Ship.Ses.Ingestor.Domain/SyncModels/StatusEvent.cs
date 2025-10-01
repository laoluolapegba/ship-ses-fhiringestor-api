using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;


namespace Ship.Ses.Ingestor.Domain.SyncModels
{
    //using Ship.Ses.Ingestor.Domain.Attributes; 

    //[BsonCollectionName("fhirstatusevents")]

    public sealed class StatusEvent : BaseMongoDocument
    {
        [BsonElement("transactionId")]
        public string TransactionId { get; set; } = default!;

        [BsonElement("resourceType")]
        public string ResourceType { get; set; } = default!; // inferred from Data.resourceType

        [BsonElement("resourceId")]
        public string? ResourceId { get; set; }                // inferred from Data.id (if present)

        [BsonElement("shipId")]
        public string ShipId { get; set; } = default!;

        [BsonElement("status")]
        public string Status { get; set; } = default!;

        [BsonElement("message")]
        public string Message { get; set; } = default!;

        [BsonElement("receivedAtUtc")]
        public DateTime ReceivedAtUtc { get; set; }

        [BsonElement("source")]
        public string Source { get; set; } = "SHIP";

        [BsonElement("headers")]
        public string? Headers { get; set; } 

        [BsonElement("payloadHash")]
        public string PayloadHash { get; set; } = default!;

        [BsonElement("data")]
        public MongoDB.Bson.BsonDocument Data { get; set; } = default!; 
        [BsonElement("correlationId")]
        public string CorrelationId { get; set; }
        // Outbox fields for EMR callback processing
        [BsonElement("callbackStatus")]
        public string CallbackStatus { get; set; } = "Pending";  // Pending|InFlight|Succeeded|Failed

        [BsonElement("callbackAttempts")]
        public int CallbackAttempts { get; set; }

        [BsonElement("callbackNextAttemptAt")]
        public DateTime? CallbackNextAttemptAt { get; set; } = DateTime.UtcNow;

        [BsonElement("callbackLastError")]
        public string? CallbackLastError { get; set; }

        [BsonElement("callbackDeliveredAt")]
        public DateTime? CallbackDeliveredAt { get; set; }

        [BsonElement("emrTargetUrl")]
        public string? EmrTargetUrl { get; set; }

        [BsonElement("emrResponseStatusCode")]
        public int? EmrResponseStatusCode { get; set; }

        [BsonElement("emrResponseBody")]
        public string? EmrResponseBody { get; set; }
        [BsonElement("clientId")]
        public string? ClientId { get; set; }
        [BsonElement("facilityId")]
        public string? FacilityId { get; set; }
        public override string CollectionName => "fhirstatusevents";
    }



}
