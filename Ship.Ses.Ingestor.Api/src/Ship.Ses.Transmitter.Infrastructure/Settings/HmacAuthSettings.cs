using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Infrastructure.Settings
{
    public sealed record HmacAuthSettings
    {
        public bool Enabled { get; init; } = true;
        public bool RequireJwtAlso { get; init; } = true;
        public string SignatureHeader { get; init; } = "X-SHIP-Signature";
        public string TimestampHeader { get; init; } = "X-SHIP-Date";
        public string NonceHeader { get; init; } = "X-SHIP-Nonce";
        public int AllowedClockSkewSeconds { get; init; } = 300;
        public string HmacAlgo { get; init; } = "HMACSHA256"; // HMACSHA256 / HMACSHA512
        public SingleClientSettings Clients { get; init; } = new();
    }
    public sealed record SingleClientSettings
    {
        public string ClientSecret { get; init; } = default!;
        public string ClientId { get; init; } = default!;
    }
}
