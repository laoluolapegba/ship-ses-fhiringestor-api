namespace Ship.Ses.Ingestor.Domain.Patients
{
    public class GenericResourceSyncRecord : FhirSyncRecord
    {
        public override string CollectionName => "transformed_pool_resources";
    }
}
