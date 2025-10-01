

namespace Ship.Ses.Ingestor.Domain.Patients
{
    public class PatientSyncRecord : FhirSyncRecord
    {
        public override string CollectionName => "transformed_pool_patients";
        public PatientSyncRecord()
        {
            ResourceType = "Patient";
        }
    }
}