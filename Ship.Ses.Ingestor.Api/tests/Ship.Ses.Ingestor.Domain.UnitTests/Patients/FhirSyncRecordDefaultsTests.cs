using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Domain.Patients;

namespace Ship.Ses.Ingestor.Domain.UnitTests.Patients
{
    public class FhirSyncRecordDefaultsTests
    {
        [Fact]
        public void PatientSyncRecord_applies_metadata_defaults_on_construction()
        {
            var record = new PatientSyncRecord();

            Assert.Null(record.TargetSystem);
            Assert.Equal(1, record.SchemaVersion);
            Assert.Equal("R4", record.FhirVersion);
            Assert.Equal("Patient", record.ResourceType);
        }

        [Fact]
        public void GenericResourceSyncRecord_applies_metadata_defaults_on_construction()
        {
            var record = new GenericResourceSyncRecord();

            Assert.Null(record.TargetSystem);
            Assert.Equal(1, record.SchemaVersion);
            Assert.Equal("R4", record.FhirVersion);
        }

        [Fact]
        public void Explicit_metadata_assignments_override_defaults()
        {
            var record = new PatientSyncRecord
            {
                TargetSystem = "PDS",
                FhirVersion = "R5",
                SchemaVersion = 2
            };

            Assert.Equal("PDS", record.TargetSystem);
            Assert.Equal("R5", record.FhirVersion);
            Assert.Equal(2, record.SchemaVersion);
        }
    }
}
