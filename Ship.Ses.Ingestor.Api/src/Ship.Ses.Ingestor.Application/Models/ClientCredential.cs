namespace Ship.Ses.Ingestor.Application.Models
{
    public sealed record ClientCredential
    {
        public required string ClientId { get; init; }
        public string? ClientSecret { get; init; }
        public string? SecretReference { get; init; }
        public bool IsActive { get; init; } = true;
        public bool IsRevoked { get; init; }
        public IReadOnlyCollection<string> AllowedAlgorithms { get; init; } = Array.Empty<string>();
    }
}
