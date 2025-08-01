using Ship.Ses.Transmitter.Infrastructure.Persistance.MySql;
using Ship.Ses.Transmitter.Infrastructure.Settings;
//using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Ship.Ses.Transmitter.Infrastructure.Installers
{
    public static class AppDbContextInstallerHostBuilder
    {


        public static void SeedDatabase(ShipServerDbContext appDbContext)
        {
            appDbContext.Database.Migrate();
        }
        public static TBuilder InstallAppDbContext<TBuilder>(this TBuilder builder) where TBuilder : IHostBuilder
        {
            builder.ConfigureServices((hostContext, services) =>
            {
                var appSettings = hostContext.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>();

                if (appSettings != null)
                {
                    var msSqlSettings = appSettings.ShipServerSqlDb;

                    services.AddDbContext<ShipServerDbContext>(options =>
                     options.UseMySQL(msSqlSettings.ConnectionString));

                     services.AddScoped<IShipServerDbContext>(provider => provider.GetService<ShipServerDbContext>());
                }
            });

            return builder;
        }
        
    }


}
