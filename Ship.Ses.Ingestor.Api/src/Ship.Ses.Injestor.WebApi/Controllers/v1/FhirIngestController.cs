using MassTransit.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Resources;
using Ship.Ses.Ingestor.Api.Helper;
using Ship.Ses.Ingestor.Api.Models;
using Ship.Ses.Ingestor.Application.Patients;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Infrastructure.Persistance;
using Ship.Ses.Ingestor.WebApi.Filters;
using Swashbuckle.AspNetCore.Annotations;
using Swashbuckle.AspNetCore.Filters;
using System.Net;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
namespace Ship.Ses.Ingestor.WebApi.Controllers.v1
{

    /// <summary>
    /// Allows EMRs to submit FHIR-compliant resources into the SHIP SES platform.
    /// </summary>
    [ApiController]
    [Asp.Versioning.ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/fhir-ingest")]
    //[SwaggerTag("Endpoints for ingesting FHIR-compliant resources from external EMRs.")]
    [Authorize]
    public class FhirIngestController : ControllerBase
    {
        private readonly IFhirIngestService _ingestService;
        private readonly ILogger<FhirIngestController> _logger;
        //private readonly IClientSyncConfigProvider _clientConfig;

        public FhirIngestController(
            IFhirIngestService ingestService,
            ILogger<FhirIngestController> logger)
           // IClientSyncConfigProvider clientConfig)
        {
            _ingestService = ingestService;
            _logger = logger;
            //_clientConfig = clientConfig;
        }

        [HttpPost]
        [Produces("application/json")]
        [SwaggerOperation(
            Summary = "Submit a FHIR resource",
            Description = "Allows external EMRs to push FHIR-compliant resource data (e.g., Patient, Encounter) into the SHIP SES ingest queue.",
            OperationId = "FhirIngest_SubmitResource",
            Tags = new[] { "FHIR Ingest" }
        )]
        [ProducesResponseType(typeof(FhirIngestAcceptedResponse), StatusCodes.Status202Accepted)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<FhirIngestAcceptedResponse>> Post([FromBody] FhirIngestRequest request)
        {
            if (request == null)
                return Problem(title: "Bad request", detail: "Request body cannot be null.",
                               statusCode: StatusCodes.Status400BadRequest);

            var rawClientId = User.FindFirst("client_id")?.Value ?? User.FindFirst("azp")?.Value;
            if (string.IsNullOrWhiteSpace(rawClientId))
                return Problem(title: "Unauthorized", detail: "Missing 'client_id' or 'azp' claim.",
                               statusCode: StatusCodes.Status401Unauthorized);

            if (string.IsNullOrWhiteSpace(request.FacilityId))
                return Problem(title: "Bad request", detail: "Missing required field: FacilityId.",
                               statusCode: StatusCodes.Status400BadRequest);

            var resourceType = request.ResourceType?.Trim();
            if (string.IsNullOrWhiteSpace(resourceType))
                return Problem(title: "Bad request", detail: "Missing required field: ResourceType.",
                               statusCode: StatusCodes.Status400BadRequest);

            var resourceId = TryGetResourceId(request);

            using var _ = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = request.CorrelationId,
                ["ClientId"] = SafeMessageHelper.Sanitize(rawClientId),
                ["FacilityId"] = SafeMessageHelper.Sanitize(request.FacilityId),
                ["ResourceType"] = SafeMessageHelper.Sanitize(resourceType),
                ["ResourceId"] = SafeMessageHelper.Sanitize(resourceId ?? "(none)")
            });

            try
            {
                var result = await _ingestService.IngestAsyncReturningExisting(request, rawClientId);

                switch (result.Outcome)
                {
                    case IdempotentInsertOutcome.Inserted:
                        _logger.LogInformation("✅ Ingested {Type} corr={Corr} client={Client} facility={Facility}",
                            resourceType, request.CorrelationId, rawClientId, request.FacilityId);
                        break;

                    case IdempotentInsertOutcome.ReattemptChangedPayload:
                        _logger.LogInformation("🔁 Re-attempt (changed payload) corr={Corr} client={Client} facility={Facility}",
                            request.CorrelationId, rawClientId, request.FacilityId);
                        break;

                    case IdempotentInsertOutcome.IdempotentRepeatSamePayload:
                        _logger.LogWarning("⚠️ Same payload re-submitted corr={Corr} client={Client} facility={Facility}",
                            request.CorrelationId, rawClientId, request.FacilityId);
                        return Problem(title: "Conflict",
                            detail: $"Identical payload already submitted for correlationId '{request.CorrelationId}'.",
                            statusCode: StatusCodes.Status409Conflict,
                            extensions: new Dictionary<string, object?> { ["correlationId"] = request.CorrelationId });
                }

                var payload = new FhirIngestAcceptedResponse
                {
                    Status = "accepted",
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    CorrelationId = request.CorrelationId,
                    Timestamp = DateTime.UtcNow
                };
                return Accepted(payload);
            }
            catch (JsonException)
            {
                return Problem(title: "Bad request", detail: "Invalid JSON payload.",
                               statusCode: StatusCodes.Status400BadRequest,
                               extensions: new Dictionary<string, object?> { ["correlationId"] = request.CorrelationId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 Unexpected error ingesting {Type} {Id}.", resourceType, resourceId ?? "(none)");
                return Problem(title: "Internal server error", detail: "Unexpected error occurred while processing the request.",
                               statusCode: StatusCodes.Status500InternalServerError,
                               extensions: new Dictionary<string, object?> { ["correlationId"] = request.CorrelationId });
            }
        }

        private static string? TryGetResourceId(FhirIngestRequest req)
        {
            // 1) If the request type already has a ResourceId property, use it
            var prop = req.GetType().GetProperty("ResourceId");
            if (prop?.GetValue(req) is string rid && !string.IsNullOrWhiteSpace(rid))
                return rid;

            // 2) Try to parse known JSON fields that might hold the FHIR resource (string-typed)
            foreach (var name in new[] { "FhirJson", "Payload", "Bundle", "FhirBundle" })
            {
                var p = req.GetType().GetProperty(name);
                if (p?.GetValue(req) is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // If it's a Bundle, look for first entry.resource.id
                        if (root.TryGetProperty("resourceType", out var rt) && rt.ValueKind == JsonValueKind.String &&
                            string.Equals(rt.GetString(), "Bundle", StringComparison.OrdinalIgnoreCase))
                        {
                            if (root.TryGetProperty("entry", out var entry) && entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 0)
                            {
                                var first = entry[0];
                                if (first.TryGetProperty("resource", out var res) &&
                                    res.ValueKind == JsonValueKind.Object &&
                                    res.TryGetProperty("id", out var idEl) &&
                                    idEl.ValueKind == JsonValueKind.String)
                                    return idEl.GetString();
                            }
                        }

                        // Otherwise look for top-level id
                        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            return id.GetString();
                    }
                    catch { /* best effort; ignore */ }
                }
            }

            return null;
        }

        private static int? TryGetPayloadSizeBytes(FhirIngestRequest req)
        {
            foreach (var name in new[] { "FhirJson", "Payload", "Bundle", "FhirBundle" })
            {
                var p = req.GetType().GetProperty(name);
                if (p?.GetValue(req) is string s) return Encoding.UTF8.GetByteCount(s);
            }
            return null;
        }
    }
}
