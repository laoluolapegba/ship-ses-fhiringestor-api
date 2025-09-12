using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Scalar.AspNetCore;
using Serilog;
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
using System.Text.Json;
using static Org.BouncyCastle.Math.EC.ECCurve;





var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
//  Configure Serilog with ElasticSearch & CorrelationId
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Logging.AddSerilog();


builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false; // don’t advertise Kestrel
    o.ConfigureHttpsDefaults(h =>
    {
        h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                       | System.Security.Authentication.SslProtocols.Tls13;
        // h.OnAuthenticate = ctx => { /* optional */ };
    });
});

// Add services to the container.
builder.Services.AddIDPAuthentication(builder.Configuration);

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
//builder.Services.ConfigureTracing(builder.Configuration);

var appSettings = builder.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>();



builder.Services.Configure<KestrelServerOptions>(
          builder.Configuration.GetSection("Kestrel"));

//builder.Services.AddScoped<IClientSyncConfigProvider, EfClientSyncConfigProvider>();

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
builder.Services.AddConfiguredRateLimiting(builder.Configuration);
//builder.InstallCors();



var app = builder.Build();
app.UseSwagger();

// Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.),
// specifying the Swagger JSON endpoint.
app.UseSwaggerUI(options =>
{
    var provider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
    foreach (var d in provider.ApiVersionDescriptions)
        options.SwaggerEndpoint($"/swagger/{d.GroupName}/swagger.json", $"FHIR Ingest API {d.GroupName.ToUpperInvariant()}");

    options.RoutePrefix = "docs"; // ← UI now at /docs
    options.DocumentTitle = "SHIP SES Ingestor – API Docs";
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    //app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(CorsInstaller.DefaultCorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.UseConfiguredRateLimiting();
var env = app.Environment;

if (env.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var ex = feature?.Error;

            var problem = CreateProblem(context, ex); // helper below

            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                                               .CreateLogger("GlobalException");
            // Log full details server-side
            logger.LogError(ex, "Unhandled exception. traceId={TraceId}", problem.Extensions["traceId"]);

            await context.Response.WriteAsJsonAsync(problem);
        });
    });

    app.UseHsts();
}
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

static ProblemDetails CreateProblem(HttpContext ctx, Exception? ex)
{
    var traceId = ctx.TraceIdentifier;

    int status;
    string title;
    string detail;

    switch (ex)
    {
        case SecurityTokenException ste:
            status = StatusCodes.Status401Unauthorized;
            title = "Invalid security token";
            detail = ste.Message;
            break;

        case UnauthorizedAccessException uae:
            status = StatusCodes.Status403Forbidden;
            title = "Forbidden";
            detail = uae.Message;
            break;

        case Exception e when e is ArgumentException || e is JsonException:
            status = StatusCodes.Status400BadRequest;
            title = "Bad request";
            detail = e.Message;
            break;

        case InvalidOperationException ioe when ioe.Message.StartsWith("Unable to resolve service", StringComparison.Ordinal):
            status = StatusCodes.Status500InternalServerError;
            title = "Dependency resolution error";
            detail = "A required service was not registered. Contact the API administrator.";
            break;

        default:
            status = StatusCodes.Status500InternalServerError;
            title = "Internal server error";
            detail = "An unexpected error occurred.";
            break;
    }

    var problem = new ProblemDetails
    {
        Type = "about:blank",
        Title = title,
        Status = status,
        Detail = detail,
        Instance = ctx.Request.Path
    };

    // add a correlation id so you can find the server log entry
    problem.Extensions["traceId"] = traceId;

    return problem;
}