using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Ship.Ses.Transmitter.Application.Interfaces;
using Ship.Ses.Transmitter.Application.Patients;
using Ship.Ses.Transmitter.Domain.Patients;
using Ship.Ses.Transmitter.Infrastructure.Persistance.Configuration.Domain;
using Ship.Ses.Transmitter.Infrastructure.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Infrastructure.Persistance
{
    public class FhirIngestService : IFhirIngestService
    {
        private readonly IMongoSyncRepository _mongoSyncRepository;
        private readonly ILogger<FhirIngestService> _logger;
        private readonly IClientSyncConfigProvider _clientConfig;
        private const string ExtractSourceApi = "API";
        public FhirIngestService(IMongoSyncRepository mongoSyncRepository, IOptions<SourceDbSettings> options, 
            ILogger<FhirIngestService> logger
            ) //IClientSyncConfigProvider clientConfig
        {
            _mongoSyncRepository = mongoSyncRepository;
            _logger = logger;
            //_clientConfig = clientConfig ;
        }

        public async Task<IdempotentInsertResult<FhirSyncRecord>> IngestAsyncReturningExisting(FhirIngestRequest request, string clientId)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
            if (string.IsNullOrWhiteSpace(request.CorrelationId)) throw new ArgumentException("CorrelationId is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.FacilityId)) throw new ArgumentException("FacilityId is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ResourceType)) throw new ArgumentException("ResourceType is required.", nameof(request));

            var canonical = request.FhirJson.ToJsonString(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var bson = BsonDocument.Parse(canonical);

            var normalizedResourceType = request.ResourceType.Trim();
            FhirSyncRecord record = string.Equals(normalizedResourceType, "Patient", StringComparison.OrdinalIgnoreCase)
                ? new PatientSyncRecord()
                : new GenericResourceSyncRecord();

            record.ClientId = clientId;
            record.FacilityId = request.FacilityId;
            record.CorrelationId = request.CorrelationId;
            record.ResourceType = normalizedResourceType;
            record.ResourceId = request.ResourceId;
            record.FhirJson = bson;
            record.PayloadHash = Sha256(canonical);
            record.CreatedDate = DateTime.UtcNow;
            record.Status = "Pending";
            record.RetryCount = 0;
            record.ExtractSource = ExtractSourceApi;
            record.TransactionId = null;
            record.ApiResponsePayload = null;
            record.LastAttemptAt = null;
            record.SyncedResourceId = null;
            record.ClientEMRCallbackUrl = request.CallbackUrl;

            if (record is PatientSyncRecord patientRecord)
            {
                var result = await _mongoSyncRepository.TryInsertIdempotentAsync(patientRecord);
                return ToBaseResult(result);
            }

            if (record is GenericResourceSyncRecord genericRecord)
            {
                var result = await _mongoSyncRepository.TryInsertIdempotentAsync(genericRecord);
                return ToBaseResult(result);
            }

            throw new NotSupportedException($"FHIR record type '{record.GetType().Name}' is not supported for ingestion.");
        }

        private static string Sha256(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        private static IdempotentInsertResult<FhirSyncRecord> ToBaseResult<T>(IdempotentInsertResult<T> result) where T : FhirSyncRecord
            => new() { Outcome = result.Outcome, Document = result.Document };
    }

    public sealed class CorrelationConflictException : Exception
    {
        public CorrelationConflictException(string correlationId, string facilityId, string clientId)
            : base($"Payload conflict for correlationId '{correlationId}' (client='{clientId}', facility='{facilityId}').") { }
    }
}


