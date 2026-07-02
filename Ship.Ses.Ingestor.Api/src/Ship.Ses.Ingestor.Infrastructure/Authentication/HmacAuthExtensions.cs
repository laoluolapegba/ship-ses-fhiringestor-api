using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    public static class HmacAuthExtensions
    {
        public static IServiceCollection AddHmacAuth(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<HmacAuthSettings>(config.GetSection("AppSettings:Hmac"));
            services.Configure<HmacClientRegistryOptions>(config.GetSection("AppSettings"));
            services.AddMemoryCache();
            services.TryAddSingleton<ConfigurationClientHmacCredentialLoader>();
            services.TryAddSingleton<InMemoryClientHmacCredentialRegistry>();
            services.TryAddSingleton<IClientHmacCredentialRegistry>(sp => sp.GetRequiredService<InMemoryClientHmacCredentialRegistry>());
            services.TryAddSingleton<IClientCredentialResolver, ClientCredentialResolver>();
            services.AddTransient<HmacAuthMiddleware>();
            return services;
        }

        public static IApplicationBuilder UseHmacAuth(this IApplicationBuilder app)
        {
            var opt = app.ApplicationServices.GetRequiredService<IOptions<HmacAuthSettings>>().Value;
            if (!opt.Enabled)
            {
                return app;
            }

            // Load every registered client's HMAC secret from configuration once, before any request is served.
            // Configuration (appsettings / environment variables) is the sole source of truth; a restart is
            // required to pick up new or rotated clients.
            var loader = app.ApplicationServices.GetRequiredService<ConfigurationClientHmacCredentialLoader>();
            var registry = app.ApplicationServices.GetRequiredService<InMemoryClientHmacCredentialRegistry>();
            registry.Initialize(loader.LoadAll());

            app.UseMiddleware<HmacAuthMiddleware>();
            return app;
        }
    }
}
