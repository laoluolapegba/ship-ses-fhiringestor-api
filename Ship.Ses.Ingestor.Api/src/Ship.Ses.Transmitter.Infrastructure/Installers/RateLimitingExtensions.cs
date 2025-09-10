using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ship.Ses.Transmitter.Domain.SyncModels;
using System.Threading.RateLimiting;

namespace Ship.Ses.Transmitter.Infrastructure.Installers
{


    public static class RateLimitingExtensions
    {
        public static IServiceCollection AddConfiguredRateLimiting(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<RateLimitingSettings>(configuration.GetSection("AppSettings:RateLimiting"));

            var settings = configuration.GetSection("AppSettings:RateLimiting")
                                        .Get<RateLimitingSettings>() ?? new();

            services.AddRateLimiter(options =>
            {
                if (!settings.Enabled) return;

                options.RejectionStatusCode = settings.RejectionStatusCode;

                var order = settings.QueueProcessingOrder.Equals("NewestFirst", StringComparison.OrdinalIgnoreCase)
                    ? QueueProcessingOrder.NewestFirst
                    : QueueProcessingOrder.OldestFirst;

                // Global limiter (applies automatically to all requests via app.UseRateLimiter())
                if (settings.PartitionBy.Equals("Ip", StringComparison.OrdinalIgnoreCase))
                {
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                    {
                        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        return RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: key,
                            factory: _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = settings.PermitLimit,
                                Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                                QueueProcessingOrder = order,
                                QueueLimit = settings.QueueLimit,
                                AutoReplenishment = true
                            });
                    });
                }
                else // "None" => single global bucket
                {
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: "global",
                            factory: _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = settings.PermitLimit,
                                Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                                QueueProcessingOrder = order,
                                QueueLimit = settings.QueueLimit,
                                AutoReplenishment = true
                            }));
                }

                options.OnRejected = async (context, token) =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RateLimiting");

                    var key = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    logger.LogWarning("🚦 Rate limit hit. Key={Key} Path={Path}",
                        key, context.HttpContext.Request.Path);

                    // Best-effort Retry-After
                    var secs = Math.Max(1, settings.WindowSeconds);
                    context.HttpContext.Response.Headers["Retry-After"] = secs.ToString();
                    if (!context.HttpContext.Response.HasStarted)
                        await context.HttpContext.Response.WriteAsync("Too many requests.", token);
                };
            });

            return services;
        }

        public static IApplicationBuilder UseConfiguredRateLimiting(this IApplicationBuilder app)
        {
            var settings = app.ApplicationServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
            if (settings.Enabled)
                app.UseRateLimiter();
            return app;
        }
    }

}
