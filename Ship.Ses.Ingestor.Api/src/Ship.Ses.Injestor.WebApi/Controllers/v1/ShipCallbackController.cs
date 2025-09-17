using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Ship.Ses.Transmitter.Application.DTOs;
using Ship.Ses.Transmitter.Application.Interfaces;
using Ship.Ses.Transmitter.Domain.SyncModels;
using Swashbuckle.AspNetCore.Annotations;
using System.Net;

namespace Ship.Ses.Ingestor.Api.Controllers.v1
{
    

    [ApiController]
    [Asp.Versioning.ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}")]
    //[SwaggerTag("Status callbacks from SHIP.")]
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
        [SwaggerOperation(
            Summary = "Patient Transmission Status Callback",
            Description = "Endpoint for receiving patient transmission status updates from SHIP.",
            OperationId = "PatientTransmissionStatusCallback",
            Tags = new[] { "Status Callbacks" }
        )]
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
            _logger.LogInformation(
    "PatientTransmission ack endpoint called with payload: {requestJson}",
    System.Text.Json.JsonSerializer.Serialize(request)
);

            if (request == null)
            {
                _logger.LogError("Received a request with a null body. Returning 400 Bad Request.");
                return BadRequest("Request body cannot be empty.");
            }

            _logger.LogDebug("Processing request for patientId: {PatientId}", request.Message); // Log key data to trace

            try
            {
                var requestHeaders = Request?.Headers?.ToDictionary(h => h.Key, h => h.Value.ToString())
                                     ?? new Dictionary<string, string>();

                //var correlationId = Request.Headers.TryGetValue("x-correlation-id", out var corr)
                //    ? corr.ToString()
                //    : Guid.NewGuid().ToString();

                var response = await _statusCallbackService.ProcessStatusUpdateAsync(
                    requestHeaders,
                    request,
                    ct);

                //response.CorrelationId = correlationId;
                _logger.LogInformation("Successfully processed status update for transactionId: {TransactionId}", request.TransactionId);
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

        [HttpGet("patient/status")]
        [SwaggerOperation(
    Summary = "Get Patient Transmission Status",
    Description = "Retrieve the transmission status of a patient by transactionId or correlationId (exactly one).",
    OperationId = "GetPatientTransmissionStatus",
    Tags = new[] { "Status Queries" }
)]
        [ProducesResponseType(typeof(PatientTransmissionStatusQueryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPatientTransmissionStatus(
    [FromQuery] string? transactionId,
    [FromQuery] string? correlationId,
    [FromQuery] bool includeData = false,
    CancellationToken ct = default)
        {
            // XOR: exactly one must be provided
            var hasTx = !string.IsNullOrWhiteSpace(transactionId);
            var hasCorr = !string.IsNullOrWhiteSpace(correlationId);
            if (hasTx == hasCorr) // both true or both false
                return BadRequest("Provide exactly one of 'transactionId' or 'correlationId'.");

            try
            {
                StatusEvent? evt = hasTx
                    ? await _statusCallbackService.GetByTransactionIdAsync(transactionId!, ct)
                    : await _statusCallbackService.GetByCorrelationIdAsync(correlationId!, ct);

                if (evt is null) return NotFound();

                var response = new PatientTransmissionStatusQueryResponse
                {
                    CorrelationId = correlationId,
                    TransactionId = evt.TransactionId,
                    ShipId = evt.ShipId,
                    Status = evt.Status,
                    Message = evt.Message,
                    ResourceType = evt.ResourceType,
                    ResourceId = evt.ResourceId,
                    ReceivedAtUtc = evt.ReceivedAtUtc
                };

                if (includeData && evt.Data != null)
                {
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
                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }


    }
}
