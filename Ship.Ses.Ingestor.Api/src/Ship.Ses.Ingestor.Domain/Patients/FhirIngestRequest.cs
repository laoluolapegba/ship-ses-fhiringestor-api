using Swashbuckle.AspNetCore.Annotations;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
namespace Ship.Ses.Ingestor.Domain.Patients
{
    /// <summary>
    /// Request payload for submitting a FHIR resource
    /// </summary>
    public class FhirIngestRequest
    {
        ///// <summary>
        ///// The FHIR resource type (e.g., Patient, Encounter)
        ///// </summary>
        //[SwaggerSchema("The type of FHIR resource to ingest.", Nullable = false)]
        //[Required(ErrorMessage = "ResourceType is required.")]
        //[StringLength(100, ErrorMessage = "ResourceType must not exceed 100 characters.")]
        //public string ResourceType { get; set; }

        /// <summary>
        /// The SHIP service endpoint (e.g., "PDS, SCR")
        /// </summary>
        [SwaggerSchema("The FHIR service endpoint to which the resource belongs.", Nullable = false)]
        [Required(ErrorMessage = "SHIP Service is required.")]
        public required string ShipService { get; set; }
        /// <summary>
        /// The client-assigned resource ID (optional)
        /// </summary>
        [SwaggerSchema("Optional EMR-assigned resource identifier.")]
        [StringLength(100, ErrorMessage = "ResourceId must not exceed 100 characters.")]
        public string ResourceId { get; set; }

        /// <summary>
        /// The actual FHIR-compliant JSON object
        /// </summary>
        [SwaggerSchema("FHIR-compliant resource body as JSON.")]
        [Required(ErrorMessage = "FhirJson is required.")]
        [DataType(DataType.MultilineText)]
        public required JsonObject FhirJson { get; set; }

        /// <summary>
        /// Optional metadata or source system indicator
        /// </summary>
        [SwaggerSchema("The client id / SHIP faciltiy ID of the source EMR.")]
        [Required(ErrorMessage = "FacilityId is required.")]
        public string FacilityId { get; set; }

        [SwaggerSchema("The callback URL of the source EMR to notify upon completion.")]
        public string CallbackUrl { get; set; }

        [SwaggerSchema("Optional correlation ID for tracing the request.")]
        [Required(ErrorMessage = "CorrelationId is required")]
        public required string CorrelationId { get; set; }

    }
}
