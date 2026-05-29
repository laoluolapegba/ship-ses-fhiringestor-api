namespace Ship.Ses.Ingestor.Infrastructure.Settings
{
    public sealed record HmacAuthSettings
    {
        public bool Enabled { get; init; } = true;
        public bool RequireJwtAlso { get; init; } = true;
        public string SignatureHeader { get; init; } = "X-SHIP-Signature";
        public string TimestampHeader { get; init; } = "X-SHIP-Date";
        public string NonceHeader { get; init; } = "X-SHIP-Nonce";
        public int AllowedClockSkewSeconds { get; init; } = 300;
        public string HmacAlgo { get; init; } = "HMACSHA256";

        /// <summary>
        /// Request paths exempt from HMAC validation (matched by path segments, case-insensitive).
        /// Defaults to the health endpoint so unauthenticated liveness/readiness probes succeed.
        /// </summary>
        public IReadOnlyList<string> BypassPaths { get; init; } = new[] { "/health" };
    }
}
