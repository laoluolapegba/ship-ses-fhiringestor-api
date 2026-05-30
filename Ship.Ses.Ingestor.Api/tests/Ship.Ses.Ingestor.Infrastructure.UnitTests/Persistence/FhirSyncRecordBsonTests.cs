using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Patients;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Persistence
{
    public class FhirSyncRecordBsonTests
    {
        [Fact]
        public void Legacy_document_without_new_metadata_deserializes_with_defaults()
        {
            // Represents a record written before targetSystem / schemaVersion / fhirVersion existed.
            // Backward compatibility requires the C# property initializers to provide defaults.
            var legacy = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "resourceType", "Patient" },
                { "resourceId", "p1" },
                { "status", "Pending" },
                { "createdDate", DateTime.UtcNow },
                { "facilityId", "fac-1" },
                { "shipService", "PDS" },
                { "correlationId", "corr-1" },
                { "clientId", "emr-a" },
                { "payloadHash", "abc123" }
            };

            var record = BsonSerializer.Deserialize<PatientSyncRecord>(legacy);

            Assert.Null(record.TargetSystem);
            Assert.Equal(1, record.SchemaVersion);
            Assert.Equal("R4", record.FhirVersion);
            Assert.Equal("emr-a", record.ClientId);
            Assert.Equal("Patient", record.ResourceType);
        }

        [Fact]
        public void Record_with_new_metadata_round_trips_through_bson()
        {
            var record = new PatientSyncRecord
            {
                Id = ObjectId.GenerateNewId().ToString(),
                ClientId = "emr-a",
                FacilityId = "fac-1",
                CorrelationId = "corr-1",
                PayloadHash = "abc",
                Status = "Pending",
                CreatedDate = DateTime.UtcNow,
                ShipService = "PDS",
                ResourceId = "p1",
                TargetSystem = "PDS",
                FhirVersion = "R5",
                SchemaVersion = 2
            };

            var bson = record.ToBsonDocument();
            var roundTripped = BsonSerializer.Deserialize<PatientSyncRecord>(bson);

            Assert.Equal("PDS", roundTripped.TargetSystem);
            Assert.Equal("R5", roundTripped.FhirVersion);
            Assert.Equal(2, roundTripped.SchemaVersion);
        }

        [Fact]
        public void Null_target_system_is_omitted_from_persisted_bson()
        {
            var record = new GenericResourceSyncRecord
            {
                Id = ObjectId.GenerateNewId().ToString(),
                ClientId = "emr-a",
                FacilityId = "fac-1",
                CorrelationId = "corr-1",
                PayloadHash = "abc",
                Status = "Pending",
                CreatedDate = DateTime.UtcNow,
                ShipService = "PDS",
                ResourceType = "Encounter",
                ResourceId = "e1",
                TargetSystem = null
            };

            var bson = record.ToBsonDocument();

            Assert.False(bson.Contains("targetSystem"));
            Assert.True(bson.Contains("schemaVersion"));
            Assert.True(bson.Contains("fhirVersion"));
        }
    }
}
