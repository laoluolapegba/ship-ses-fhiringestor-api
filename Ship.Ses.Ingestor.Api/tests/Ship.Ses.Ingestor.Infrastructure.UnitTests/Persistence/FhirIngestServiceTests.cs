using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Infrastructure.Persistance;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.Infrastructure.UnitTests.Persistence
{
    public class FhirIngestServiceTests
    {
        [Theory]
        [InlineData("PDS")]
        [InlineData("SCR")]
        [InlineData("FHIR-Gateway")]
        public async Task TargetSystem_mirrors_ShipService(string shipService)
        {
            var captured = await IngestPatientAndCapture(BuildRequest(shipService));

            Assert.Equal(shipService, captured.TargetSystem);
            Assert.Equal(shipService, captured.ShipService);
        }

        [Fact]
        public async Task SchemaVersion_and_FhirVersion_use_entity_defaults()
        {
            var captured = await IngestPatientAndCapture(BuildRequest("PDS"));

            Assert.Equal(1, captured.SchemaVersion);
            Assert.Equal("R4", captured.FhirVersion);
        }

        [Fact]
        public async Task Generic_resource_path_also_sets_TargetSystem_from_ShipService()
        {
            var request = BuildRequest("PDS", resourceType: "Encounter");

            GenericResourceSyncRecord? captured = null;
            var repo = new Mock<IMongoSyncRepository>();
            repo.Setup(r => r.TryInsertIdempotentAsync(It.IsAny<GenericResourceSyncRecord>()))
                .Callback<GenericResourceSyncRecord>(r => captured = r)
                .ReturnsAsync(() => new IdempotentInsertResult<GenericResourceSyncRecord>
                {
                    Outcome = IdempotentInsertOutcome.Inserted,
                    Document = captured!
                });

            var service = CreateService(repo);
            await service.IngestAsyncReturningExisting(request, "emr-a");

            Assert.NotNull(captured);
            Assert.Equal("PDS", captured!.TargetSystem);
            Assert.Equal(1, captured.SchemaVersion);
            Assert.Equal("R4", captured.FhirVersion);
        }

        private static async Task<PatientSyncRecord> IngestPatientAndCapture(FhirIngestRequest request)
        {
            PatientSyncRecord? captured = null;
            var repo = new Mock<IMongoSyncRepository>();
            repo.Setup(r => r.TryInsertIdempotentAsync(It.IsAny<PatientSyncRecord>()))
                .Callback<PatientSyncRecord>(r => captured = r)
                .ReturnsAsync(() => new IdempotentInsertResult<PatientSyncRecord>
                {
                    Outcome = IdempotentInsertOutcome.Inserted,
                    Document = captured!
                });

            var service = CreateService(repo);
            await service.IngestAsyncReturningExisting(request, "emr-a");

            Assert.NotNull(captured);
            return captured!;
        }

        private static FhirIngestService CreateService(Mock<IMongoSyncRepository> repo)
            => new(
                repo.Object,
                Options.Create(new SourceDbSettings { ConnectionString = "x", DatabaseName = "y" }),
                NullLogger<FhirIngestService>.Instance);

        private static FhirIngestRequest BuildRequest(string shipService, string resourceType = "Patient")
        {
            var json = $$"""{ "resourceType": "{{resourceType}}", "id": "x1" }""";
            return new FhirIngestRequest
            {
                ShipService = shipService,
                ResourceId = "x1",
                FacilityId = "fac-1",
                CorrelationId = "corr-1",
                CallbackUrl = "https://callback.example/",
                FhirJson = JsonNode.Parse(json)!.AsObject()
            };
        }
    }
}
