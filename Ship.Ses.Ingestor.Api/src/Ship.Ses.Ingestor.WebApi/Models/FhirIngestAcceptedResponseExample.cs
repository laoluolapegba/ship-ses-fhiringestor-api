namespace Ship.Ses.Ingestor.Api.Models
{
    public class FhirIngestAcceptedResponseExample : Swashbuckle.AspNetCore.Filters.IExamplesProvider<FhirIngestAcceptedResponse>
    {
        public FhirIngestAcceptedResponse GetExamples() => new()
        {
            Status = "accepted",
            ResourceType = "Patient",
            ResourceId = "pat-123",
            CorrelationId = "c9f6c0b7-0b5a-4b2e-9b8e-0f1e1b2c3d4e",
            Timestamp = DateTime.UtcNow
        };
    }

}
