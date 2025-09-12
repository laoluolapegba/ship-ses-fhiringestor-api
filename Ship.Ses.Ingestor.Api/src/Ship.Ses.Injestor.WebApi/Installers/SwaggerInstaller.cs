using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Api.Models;
using Swashbuckle.AspNetCore.Filters;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.IO;
using System.Reflection;

namespace Ship.Ses.Transmitter.WebApi.Installers
{
    public static class SwaggerInstaller
    {
        public static void InstallSwagger(this WebApplicationBuilder builder)
        {
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddControllers();

            // Register example providers (scan the assembly that contains the example type)
            builder.Services.AddSwaggerExamplesFromAssemblyOf<FhirIngestAcceptedResponseExample>();

            // If you already have custom swagger options elsewhere, keep this
            builder.Services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();

            builder.Services.AddSwaggerGen(options =>
            {
                options.EnableAnnotations();

                // Enable examples support
                options.ExampleFilters();

                // XML comments (optional but nice)
                var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
                if (File.Exists(xmlPath))
                    options.IncludeXmlComments(xmlPath);

                // Your existing security setup
                options.AddSwaggerSecurityDefinition();
            });
        }
    }

}
