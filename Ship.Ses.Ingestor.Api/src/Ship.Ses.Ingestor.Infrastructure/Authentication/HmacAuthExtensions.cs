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
            services.AddMemoryCache();
            services.AddHttpClient<VaultClientHmacCredentialRegistry>();
            services.TryAddSingleton<IClientHmacCredentialRegistry>(sp => sp.GetRequiredService<VaultClientHmacCredentialRegistry>());
            services.TryAddSingleton<IClientCredentialResolver, ClientCredentialResolver>();
            services.AddTransient<HmacAuthMiddleware>();
            return services;
        }

        public static IApplicationBuilder UseHmacAuth(this IApplicationBuilder app)
        {
            var opt = app.ApplicationServices.GetRequiredService<IOptions<HmacAuthSettings>>().Value;
            if (opt.Enabled) app.UseMiddleware<HmacAuthMiddleware>();
            return app;
        }
    }
}
