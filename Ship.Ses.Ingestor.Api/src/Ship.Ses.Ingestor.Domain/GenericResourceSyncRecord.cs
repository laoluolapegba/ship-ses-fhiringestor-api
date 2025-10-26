using Ship.Ses.Ingestor.Domain.Patients;

namespace Ship.Ses.Ingestor.Domain
{
    public class GenericResourceSyncRecord : FhirSyncRecord
    {
        public override string CollectionName => "transformed_pool_resources";
    }
}
