using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlX.XDevAPI;
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

        public async Task<IdempotentInsertResult<PatientSyncRecord>> IngestAsyncReturningExisting(FhirIngestRequest request, string clientId)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
            if (string.IsNullOrWhiteSpace(request.CorrelationId)) throw new ArgumentException("CorrelationId is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.FacilityId)) throw new ArgumentException("FacilityId is required.", nameof(request));

            var canonical = request.FhirJson.ToJsonString(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var bson = BsonDocument.Parse(canonical);

            var record = new PatientSyncRecord
            {
                ClientId = clientId,
                FacilityId = request.FacilityId,
                CorrelationId = request.CorrelationId,
                ResourceType = request.ResourceType,
                ResourceId = request.ResourceId,
                FhirJson = bson,
                PayloadHash = Sha256(canonical),
                CreatedDate = DateTime.UtcNow,
                Status = "Pending",
                RetryCount = 0,
                ExtractSource = "API",
                TransactionId = null,
                ApiResponsePayload = null,
                LastAttemptAt = null,
                SyncedResourceId = null,
                ClientEMRCallbackUrl = request.CallbackUrl
            };

            var result = await _mongoSyncRepository.TryInsertIdempotentAsync(record);
            return result;
        }

        private static string Sha256(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }
    }

    public sealed class CorrelationConflictException : Exception
    {
        public CorrelationConflictException(string correlationId, string facilityId, string clientId)
            : base($"Payload conflict for correlationId '{correlationId}' (client='{clientId}', facility='{facilityId}').") { }
    }
}


