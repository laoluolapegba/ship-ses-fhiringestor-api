namespace Ship.Ses.Ingestor.Infrastructure.Settings
{
    public record Authentication(string Authority, string Audience, string ClientId, string MetadataUrl, string TokenEndpoint);

}
