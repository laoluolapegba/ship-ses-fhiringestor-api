namespace Ship.Ses.Ingestor.Application.Shared
{
    public sealed record IntegrationEvent(Guid IntergrationEventId, DateTime OccuredAt, string Type, string AssemblyName, string Payload, DateTime? PublishedAt = null);
}
