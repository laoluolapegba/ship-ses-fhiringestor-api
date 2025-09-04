using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Scalar.AspNetCore;
using Ship.Ses.Transmitter.Application.Interfaces;
using Ship.Ses.Transmitter.Application.Patients;
using Ship.Ses.Transmitter.Application.Services;
using Ship.Ses.Transmitter.Application.Shared;
using Ship.Ses.Transmitter.Domain.Patients;
using Ship.Ses.Transmitter.Infrastructure.Installers;
using Ship.Ses.Transmitter.Infrastructure.Persistance;
using Ship.Ses.Transmitter.Infrastructure.Persistance.Configuration.Domain;
using Ship.Ses.Transmitter.Infrastructure.Persistance.Configuration.Domain.Sync;
using Ship.Ses.Transmitter.Infrastructure.Persistance.MySql;
using Ship.Ses.Transmitter.Infrastructure.Settings;
using Ship.Ses.Transmitter.Infrastructure.Shared;
using Ship.Ses.Transmitter.WebApi.Installers;
var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
builder.Services.AddOktaAuthentication(builder.Configuration);

// Configure SourceDbSettings from appsettings.json
builder.Services.Configure<SourceDbSettings>(builder.Configuration.GetSection("SourceDbSettings"));

// Register IMongoClient as a Singleton
builder.Services.AddSingleton<IMongoClient>(s =>
{
    // Get SourceDbSettings via IOptions<SourceDbSettings>
    var settings = s.GetRequiredService<IOptions<SourceDbSettings>>().Value; 

    if (string.IsNullOrEmpty(settings.ConnectionString)) 
    {
        throw new InvalidOperationException("SourceDbSettings:ConnectionString is not configured."); 
    }
    return new MongoClient(settings.ConnectionString); 
});
// Register IMongoSyncRepository as a Scoped service
builder.Services.AddScoped<IMongoSyncRepository, MongoSyncRepository>();
builder.Services.AddScoped<IHealthService, HealthService>();
builder.Services.AddScoped<IStatusEventRepository, StatusEventRepository>();

builder.Services.AddScoped<IStatusCallbackService, StatusCallbackService>();

// Register Services & Observability
builder.Services.ConfigureTracing(builder.Configuration);

var appSettings = builder.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>();
if (appSettings != null)
{

    var msSqlSettings = appSettings.ShipServerSqlDb;
    builder.Services.AddDbContext<ShipServerDbContext>(options =>
    {
        options.UseMySQL(msSqlSettings.ConnectionString);
    });
}
else
{
    throw new Exception("AppSettings not found");
}

builder.Services.AddScoped<IClientSyncConfigProvider, EfClientSyncConfigProvider>();

// Register IFhirIngestService 
builder.Services.AddScoped<IFhirIngestService, FhirIngestService>();
//builder.InstallEntityFramework();
builder.Services.AddControllers();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0); // Default version if not specified
    options.AssumeDefaultVersionWhenUnspecified = true; // Use the default version when no version is specified
    options.ReportApiVersions = true; // Report API versions in the response headers

    options.ApiVersionReader = ApiVersionReader.Combine(
        new QueryStringApiVersionReader("api-version"),
        new HeaderApiVersionReader("X-API-Version"),
        new UrlSegmentApiVersionReader()); // Enables versioning in URL path (e.g., /v1/resource)
});


// Configure API versioning
builder.Services.AddApiVersioning(options =>
{
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.ReportApiVersions = true;
})
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});




builder.InstallSwagger();


builder.InstallApplicationSettings();

builder.InstallDependencyInjectionRegistrations();
builder.Services.AddOpenApi();
builder.InstallCors();

var app = builder.Build();
app.UseSwagger();

// Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.),
// specifying the Swagger JSON endpoint.
app.UseSwaggerUI(options =>
{
    // Build a swagger endpoint for each discovered API version
    foreach (var description in app.Services.GetRequiredService<IApiVersionDescriptionProvider>().ApiVersionDescriptions)
    {
        options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", $"FHIR Ingest API {description.GroupName.ToUpperInvariant()}");
    }
    options.RoutePrefix = "swagger"; 
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(CorsInstaller.DefaultCorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

if (addresses != null)
{
    foreach (var address in addresses.Addresses)
    {
        app.Logger.LogInformation("✅ Application is listening on: {Address}", address);
    }
}
else
{
    app.Logger.LogWarning("⚠️ Could not determine server addresses (no IServerAddressesFeature found).");
}

app.Logger.LogInformation("SHIP SeS Ingestor API started and ready to accept requests");


app.Run();
