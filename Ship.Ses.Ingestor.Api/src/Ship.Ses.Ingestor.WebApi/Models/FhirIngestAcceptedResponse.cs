namespace Ship.Ses.Ingestor.Api.Models
{
    public sealed class FhirIngestAcceptedResponse
    {
        public string Status { get; init; } = "accepted";
        //public string ResourceType { get; init; } = default!;
        public string? ResourceId { get; init; }
        public string? CorrelationId { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    }

}
