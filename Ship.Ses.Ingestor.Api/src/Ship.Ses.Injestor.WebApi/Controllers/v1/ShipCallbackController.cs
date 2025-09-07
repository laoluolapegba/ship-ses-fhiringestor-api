using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Ship.Ses.Transmitter.Application.DTOs;
using Ship.Ses.Transmitter.Application.Interfaces;
using System.Net;

namespace Ship.Ses.Ingestor.Api.Controllers.v1
{
    

    [ApiController]
    [Asp.Versioning.ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}")]
    public class ShipCallbackController : ControllerBase
    {
        private readonly IStatusCallbackService _statusCallbackService;
        private readonly ILogger<ShipCallbackController> _logger;
        //https://myfacility.health.ng/ship/fhir/ack
        public ShipCallbackController(IStatusCallbackService statusCallbackService, ILogger<ShipCallbackController> logger)
        {
            _statusCallbackService = statusCallbackService;
            _logger = logger;
        }

        [HttpPost("patient/ack")]
        [ProducesResponseType(typeof(PatientTransmissionStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> PatientTransmissionStatus([FromBody] PatientTransmissionStatusRequest request, CancellationToken ct)
        {
            // Optional hard checks :
            // if (!Request.Headers.TryGetValue("x-correlation-id", out var corr)) return BadRequest("x-correlation-id header is required.");
            // if (!Request.Headers.TryGetValue("x-client-id", out var clientId)) return Unauthorized("x-client-id header is missing or invalid.");
            _logger.LogInformation("PatientTransmissionStatus endpoint called.");

            // **CRITICAL FIX: Check if the request body is null.**
            if (request == null)
            {
                _logger.LogError("Received a request with a null body. Returning 400 Bad Request.");
                return BadRequest("Request body cannot be empty.");
            }

            _logger.LogDebug("Processing request for patientId: {PatientId}", request.Message); // Log key data to trace

            try
            {
                // This line is where the exception would have occurred.
                var requestHeaders = Request?.Headers?.ToDictionary(h => h.Key, h => h.Value.ToString())
                                     ?? new Dictionary<string, string>();

                var correlationId = Request.Headers.TryGetValue("x-correlation-id", out var corr)
                    ? corr.ToString()
                    : Guid.NewGuid().ToString();

                var response = await _statusCallbackService.ProcessStatusUpdateAsync(
                    requestHeaders,
                    request,
                    ct);

                response.CorrelationId = correlationId;
                _logger.LogInformation("Successfully processed status update for correlationId: {CorrelationId}", correlationId);
                return Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Conflict error: {Message}", ex.Message);
                return Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Bad request error: {Message}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred.");
                return StatusCode(500, "An unexpected error occurred: " + ex.Message);
            }
        }

        [HttpGet("patient/status/{transactionId}")]
        [ProducesResponseType(typeof(PatientTransmissionStatusQueryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPatientTransmissionStatus(
    [FromRoute] string transactionId,
    [FromQuery] bool includeData = false,
    CancellationToken ct = default)
        {
            // Correlation ID: use inbound header if present, else generate
            var correlationId = Request.Headers.TryGetValue("x-correlation-id", out var corr)
                ? corr.ToString()
                : Guid.NewGuid().ToString();

            if (string.IsNullOrWhiteSpace(transactionId))
                return BadRequest("transactionId is required.");

            try
            {
                var evt = await _statusCallbackService.GetByTransactionIdAsync(transactionId, ct);
                // If your service doesn’t expose this, inject IStatusEventRepository and call it directly:
                // var evt = await _repository.GetByTransactionIdAsync(transactionId, ct);

                if (evt is null)
                    return NotFound();

                var response = new PatientTransmissionStatusQueryResponse
                {
                    CorrelationId = correlationId,
                    TransactionId = evt.TransactionId,
                    ShipId = evt.ShipId,
                    Status = evt.Status,
                    Message = evt.Message,
                    ResourceType = evt.ResourceType,
                    ResourceId = evt.ResourceId,
                    ReceivedAtUtc = evt.ReceivedAtUtc,

                    //CallbackStatus = evt.CallbackStatus,
                    //CallbackAttempts = evt.CallbackAttempts,
                    //CallbackNextAttemptAt = evt.CallbackNextAttemptAt,
                    //CallbackDeliveredAt = evt.CallbackDeliveredAt,
                    //EmrTargetUrl = evt.EmrTargetUrl,
                    //EmrResponseStatusCode = evt.EmrResponseStatusCode,
                    //EmrResponseBody = evt.EmrResponseBody
                };

                if (includeData && evt.Data != null)
                {
                    // Convert stored BsonDocument (evt.Data) to JsonObject for the response
                    var json = evt.Data.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
                    {
                        OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson
                    });
                    response.Data = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                // Optional: log with correlationId in scope if you use Serilog’s LogContext
                // using (LogContext.PushProperty("CorrelationId", correlationId)) { _logger.LogError(ex, ...); }
                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }

    }
}
