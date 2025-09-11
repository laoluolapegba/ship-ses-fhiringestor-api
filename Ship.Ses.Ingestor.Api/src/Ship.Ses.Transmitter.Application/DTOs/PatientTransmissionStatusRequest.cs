using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Application.DTOs
{
    public sealed class PatientTransmissionStatusCallbackRequest
    {
        
    }
    public class PatientTransmissionStatusRequest
    {
        [Required]
        [RegularExpression("^(SUCCESS|FAILED|PENDING)$")]
        public string Status { get; set; } = default!;

        [Required, StringLength(2000, MinimumLength = 1)]
        public string Message { get; set; } = default!;

        //[RegularExpression(@"^SHIP[0-9]{10,}$")]
        public string ShipId { get; set; } = default!;

        [Required, MinLength(1)]
        public string TransactionId { get; set; } = default!;

        //[Required]
        public System.Text.Json.Nodes.JsonObject Data { get; set; } = default!;

        // Optional client-sent time (server falls back to UtcNow)
        public DateTimeOffset? Timestamp { get; set; }
    }
    public enum StatusEnum
    {
        SUCCESS,
        FAILED,
        PENDING
    }
}
