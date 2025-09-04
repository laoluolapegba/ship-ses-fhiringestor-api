using Microsoft.AspNetCore.Mvc;
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
        //https://myfacility.health.ng/ship/fhir/ack
        public ShipCallbackController(IStatusCallbackService statusCallbackService)
        {
            _statusCallbackService = statusCallbackService;
        }

        [HttpPost("patient/ack")]
        [ProducesResponseType(typeof(PatientTransmissionStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> PatientTransmissionStatus([FromBody] PatientTransmissionStatusRequest request, CancellationToken ct)
        {
            // Optional hard checks – enable if you want strict headers:
            // if (!Request.Headers.TryGetValue("x-correlation-id", out var corr)) return BadRequest("x-correlation-id header is required.");
            // if (!Request.Headers.TryGetValue("x-client-id", out var clientId)) return Unauthorized("x-client-id header is missing or invalid.");

            try
            {
                // Headers are now optional
                var requestHeaders = Request?.Headers?.ToDictionary(h => h.Key, h => h.Value.ToString())
                                    ?? new Dictionary<string, string>();

                // Correlation id is optional; generate if absent
                var correlationId = Request.Headers.TryGetValue("x-correlation-id", out var corr)
                    ? corr.ToString()
                    : Guid.NewGuid().ToString();

                var response = await _statusCallbackService.ProcessStatusUpdateAsync(
                    requestHeaders,
                    request,
                    ct);

                response.CorrelationId = correlationId;
                return Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "An unexpected error occurred: " + ex.Message);
            }
        }
    }
}
