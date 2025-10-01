using Ship.Ses.Ingestor.Application.Shared;
using Ship.Ses.Ingestor.Domain;
using Ship.Ses.Ingestor.Infrastructure.ReadServices;
using Ship.Ses.Ingestor.Infrastructure.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ship.Ses.Ingestor.Infrastructure.Installers
{
    public static class DependencyInjectionInstaller
    {
        public static void InstallDependencyInjectionRegistrations(this WebApplicationBuilder builder)
        {
            builder.Services.AddHttpContextAccessor();
            //builder.Services.AddScoped<IOrderRepository, OrderRepository>();
            //builder.Services.AddTransient<IDateTimeProvider, DateTimeProvider>();
            //builder.Services.AddScoped<IOrderReadService, OrderReadService>();


        }

    }
}
