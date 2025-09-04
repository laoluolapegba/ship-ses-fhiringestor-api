using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;


namespace Ship.Ses.Transmitter.Domain.SyncModels
{
    //using Ship.Ses.Transmitter.Domain.Attributes; 

    //[BsonCollectionName("patientstatusevents")]

    public sealed class StatusEvent : BaseMongoDocument
    {
        [BsonElement("transactionId")]
        public string TransactionId { get; set; } = default!;

        [BsonElement("resourceType")]
        public string ResourceType { get; set; } = "Patient"; // inferred from Data.resourceType

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
        public string? Headers { get; set; }                    // raw serialized request headers (optional)

        [BsonElement("payloadHash")]
        public string PayloadHash { get; set; } = default!;     // canonical hash to detect conflicts

        [BsonElement("data")]
        public MongoDB.Bson.BsonDocument Data { get; set; } = default!; // full Patient JSON

        public override string CollectionName => "patientstatusevents";
    }



}
