namespace Ship.Ses.Ingestor.Infrastructure.Settings
{
    /// <summary>
    /// Binds the set of registered HMAC clients from configuration (the <c>AppSettings:Clients</c> array).
    /// In deployment these values are supplied through environment variables using the standard
    /// double-underscore convention, e.g. <c>AppSettings__Clients__0__ClientId</c>,
    /// <c>AppSettings__Clients__0__HmacSecret</c>. No Vault address or token is required.
    /// </summary>
    public sealed class HmacClientRegistryOptions
    {
        public List<HmacClientDefinition> Clients { get; init; } = new();
    }

    /// <summary>
    /// A single client's credentials as declared in configuration.
    /// <see cref="HmacSecret"/> is the key used to verify request signatures; <see cref="ClientSecret"/>
    /// is the client's OAuth secret and is carried for completeness but not used by HMAC validation.
    /// </summary>
    public sealed class HmacClientDefinition
    {
        public string? ClientId { get; init; }
        public string? ClientSecret { get; init; }
        public string? HmacSecret { get; init; }
        public string? Status { get; init; }
    }
}
