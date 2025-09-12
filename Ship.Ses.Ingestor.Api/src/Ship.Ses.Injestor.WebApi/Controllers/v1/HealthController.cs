using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Ship.Ses.Transmitter.Application.DTOs;
using Ship.Ses.Transmitter.Application.Shared;
using Ship.Ses.Transmitter.Domain;
using Swashbuckle.AspNetCore.Annotations;
using System.Diagnostics;
using System.Reflection;

namespace Ship.Ses.Transmitter.WebApi.Controllers.v1
{
    [ApiController]
    [Route("/health")] 
    [ApiVersionNeutral] 
    //[SwaggerTag("Endpoint to check the health status of the SHIP SES Ingestor API.")]
    public class HealthController : ControllerBase
    {
        // inject services here that perform actual health checks
        // private readonly IDatabaseHealthCheckService _dbHealthCheck;
        // private readonly IMessageBrokerHealthCheckService _mbHealthCheck;

        //public HealthController()
        //{
        //    // _dbHealthCheck = dbHealthCheck;
        //    // _mbHealthCheck = mbHealthCheck;
        //}
        private readonly IHealthService _healthService;
        private readonly ILogger<HealthController> _logger;

        public HealthController(IHealthService healthService, ILogger<HealthController> logger)
        {
            _healthService = healthService;
            _logger = logger;
        }

        [HttpGet]
        [SwaggerOperation(
            Summary = "Get API health status",
            Description = "Checks the overall health of the API and its dependencies.",
            OperationId = "GetHealthStatus",
            Tags = new[] { "Health" })]
        [ProducesResponseType(typeof(ApiHealthStatusDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Get()
        {
            var health = await _healthService.CheckHealthAsync();

            return health.Status switch
            {
                HealthStatus.Healthy => Ok(new { status = "healthy" }),
                HealthStatus.Degraded => StatusCode(206, new { status = "degraded", reason = health.Reason }),
                HealthStatus.Unhealthy => StatusCode(503, new { status = "unhealthy", reason = health.Reason }),
                _ => StatusCode(500, new { status = "unknown", reason = "Unexpected status" })
            };
        }

        //[HttpGet]
        //[ProducesResponseType(typeof(ApiHealthStatusDto), StatusCodes.Status200OK)]
        //[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        //public async Task<IActionResult> GetHealthStatus1()
        //{
        //    var stopwatch = Stopwatch.StartNew();
        //    var healthStatus = new ApiHealthStatusDto
        //    {
        //        CheckedAtUtc = DateTime.UtcNow,
        //        ApiVersion = "1.0.0",
        //        OverallStatus = "Healthy"
        //    };

        //    // Database health check
        //    var dbComponent = new ComponentHealthStatusDto { Name = "Database" };
        //    var dbStopwatch = Stopwatch.StartNew();
        //    try
        //    {
        //        bool isDbHealthy = true; // Replace with actual check

        //        dbComponent.Status = isDbHealthy ? "Healthy" : "Unhealthy";
        //        dbComponent.Description = isDbHealthy
        //            ? "Database connection successful."
        //            : "Failed to connect to the database.";

        //        if (!isDbHealthy)
        //            healthStatus.OverallStatus = "Unhealthy";
        //    }
        //    catch (Exception ex)
        //    {
        //        dbComponent.Status = "Unhealthy";
        //        dbComponent.Description = "Database check failed.";
                                                                    
        //        Console.Error.WriteLine($"[ERROR] DB Health Check: {ex.Message}");
        //        healthStatus.OverallStatus = "Unhealthy";
        //    }
        //    finally
        //    {
        //        dbStopwatch.Stop();
        //        dbComponent.DurationMilliseconds = dbStopwatch.ElapsedMilliseconds;
        //        healthStatus.Components.Add(dbComponent);
        //    }

        //    // Message broker health check
        //    var mbComponent = new ComponentHealthStatusDto { Name = "Message Broker" };
        //    var mbStopwatch = Stopwatch.StartNew();
        //    try
        //    {
        //        bool isMbHealthy = true; // Replace with actual check

        //        mbComponent.Status = isMbHealthy ? "Healthy" : "Degraded";
        //        mbComponent.Description = isMbHealthy
        //            ? "Message broker connected."
        //            : "Could not send test message to broker.";

        //        if (!isMbHealthy && healthStatus.OverallStatus != "Unhealthy")
        //            healthStatus.OverallStatus = "Degraded";
        //    }
        //    catch (Exception ex)
        //    {
        //        mbComponent.Status = "Unhealthy";
        //        mbComponent.Description = "Message broker check failed.";
        //        Console.Error.WriteLine($"[ERROR] Message Broker Check: {ex.Message}");
        //        healthStatus.OverallStatus = "Unhealthy";
        //    }
        //    finally
        //    {
        //        mbStopwatch.Stop();
        //        mbComponent.DurationMilliseconds = mbStopwatch.ElapsedMilliseconds;
        //        healthStatus.Components.Add(mbComponent);
        //    }

        //    // Final result
        //    stopwatch.Stop();
        //    healthStatus.TotalDurationMilliseconds = stopwatch.ElapsedMilliseconds;

        //    return healthStatus.OverallStatus == "Unhealthy"
        //        ? StatusCode(StatusCodes.Status503ServiceUnavailable, healthStatus)
        //        : Ok(healthStatus);
        //}
    }
}
