using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Ship.Ses.Ingestor.Application.DTOs;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Domain.Events;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Domain.SyncModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Services
{
    public class StatusCallbackService : IStatusCallbackService
    {
        private readonly ILogger<StatusCallbackService> _logger;
        private readonly IStatusEventRepository _repository;
        private readonly IMongoSyncRepository _mongoRepo; // <-- NEW

        public StatusCallbackService(
            ILogger<StatusCallbackService> logger,
            IStatusEventRepository repository,
            IMongoSyncRepository mongoRepo) 
        {
            _logger = logger;
            _repository = repository;
            _mongoRepo = mongoRepo;
        }

        public async Task<PatientTransmissionStatusResponse> ProcessStatusUpdateAsync(
            Dictionary<string, string> requestHeaders,
            PatientTransmissionStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentException("Request cannot be null.");
            if (string.IsNullOrWhiteSpace(request.TransactionId)) throw new ArgumentException("TransactionId is required.");

            _logger.LogInformation("Processing status update for transactionId: {TransactionId}", request.TransactionId);

            // Resolve correlation/client/facility from the original ingest, using the transactionId
            var syncRecord = await _mongoRepo.GetByTransactionIdAsync(request.TransactionId, cancellationToken);
            if (syncRecord is null)
            {
                _logger.LogWarning(
                    "No FHIR sync record found for transactionId {TxId}. Proceeding without correlationId.",
                    request.TransactionId);
            }

            var correlationId = syncRecord?.CorrelationId;
            var clientId = syncRecord?.ClientId;
            var facilityId = syncRecord?.FacilityId;

            const string HeaderResourceType = "x-fhir-resource-type";
            const string HeaderResourceId = "x-fhir-resource-id";

            var headerResourceType = TryGetHeader(requestHeaders, HeaderResourceType);
            var headerResourceId = TryGetHeader(requestHeaders, HeaderResourceId);

            var resourceType = syncRecord?.ResourceType ?? headerResourceType;
            resourceType ??= "Unknown";
            var resourceId = syncRecord?.ResourceId ?? headerResourceId ?? request.TransactionId;

            //if (resourceId is null)
            //    _logger.LogDebug("No resource ID supplied in headers for transactionId {TxId}.", request.TransactionId);

            _logger.LogInformation(
                "Using resourceType '{ResourceType}', resourceId '{ResourceId}', correlationId '{CorrelationId}', clientId '{ClientId}', facilityId '{FacilityId}' for tx {TxId}.",
                resourceType, resourceId, correlationId ?? "(none)", clientId ?? "(none)", facilityId ?? "(none)", request.TransactionId
            );

            //Hash over stable fields
            string? dataCanonical = null;
            var payloadHash = ComputePayloadHash(
                status: request.Status,
                message: request.Message,
                shipId: request.ShipId,
                txId: request.TransactionId,
                dataCanonical: dataCanonical
            );

            _logger.LogDebug("Computed payload hash: {PayloadHash}", payloadHash);

            //Build status event including correlation/client/facility if available
            var newEvent = new StatusEvent
            {
                TransactionId = request.TransactionId,
                CorrelationId = correlationId,
                ClientId = clientId,
                FacilityId = facilityId,
                ResourceType = resourceType,
                ResourceId = resourceId,
                ShipId = request.ShipId,
                Status = request.Status,
                Message = request.Message,
                ReceivedAtUtc = (request.Timestamp ?? DateTimeOffset.UtcNow).UtcDateTime,
                Source = "SHIP",
                Headers = (requestHeaders?.Count ?? 0) > 0 ? JsonSerializer.Serialize(requestHeaders) : null,
                PayloadHash = payloadHash,
                Data = null,
                CallbackStatus = "Pending",
                CallbackAttempts = 0,
                CallbackNextAttemptAt = DateTime.UtcNow,
                CallbackLastError = null,
                EmrTargetUrl = syncRecord?.ClientEMRCallbackUrl,
                CallbackDeliveredAt = null,
                EmrResponseBody = null,
                EmrResponseStatusCode = null

            };

            //) Upsert with  existing repo logic (unique on transactionId or (transactionId, source))
            var (persisted, duplicate, conflict) = await _repository.UpsertStatusEventAsync(newEvent, cancellationToken);

            if (conflict)
            {
                _logger.LogError("Transaction Status already accepted for transactionId {TxId}. The incoming payload conflicts with an existing record.", request.TransactionId);
                throw new InvalidOperationException("Conflicting payload for the same transactionId; existing record retained.");
            }
            else if (duplicate)
            {
                _logger.LogInformation("Callback received for transactionId {TxId}. record updated.", request.TransactionId);
            }
            else
            {
                _logger.LogInformation("Successfully recorded new status event for transactionId {TxId}.", request.TransactionId);
            }

            return new PatientTransmissionStatusResponse
            {
                TransactionId = persisted.TransactionId,
                StatusRecorded = persisted.Status,
                Duplicate = duplicate,
                RecordedAt = new DateTimeOffset(persisted.ReceivedAtUtc, TimeSpan.Zero)
            };
        }

        public Task<StatusEvent?> GetByTransactionIdAsync(string transactionId, CancellationToken ct = default) =>
            _repository.GetByTransactionIdAsync(transactionId, ct);

        public Task<StatusEvent?> GetByCorrelationIdAsync(string correlationId, CancellationToken ct = default) =>
            _repository.GetByCorrelationIdAsync(correlationId, ct);

        private static string? TryGetHeader(IDictionary<string, string>? headers, string key)
        {
            if (headers is null || headers.Count == 0) return null;
            // Case-insensitive lookup
            foreach (var kvp in headers)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(kvp.Value) ? null : kvp.Value;
            }
            return null;
        }

        private static string ComputePayloadHash(string status, string message, string shipId, string txId, string dataCanonical)
        {
            static string Sha256(string s)
            {
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s)); return Convert.ToHexString(bytes);
            }
            var canonical = JsonSerializer.Serialize(new
            {
                status,
                message,
                shipId,
                transactionId = txId,
                dataSha256 = string.IsNullOrEmpty(dataCanonical) ? null : Sha256(dataCanonical)
            });
            return Sha256(canonical);
        }
    }
    


    public static class PayloadHash
    {
        public static string Compute(string status, string message, string shipId, string transactionId, string dataCanonical)
        {
            var canonical = JsonSerializer.Serialize(new
            {
                status,
                message,
                shipId,
                transactionId,
                dataSha256 = Sha256(dataCanonical) // hash data to keep the outer hash bounded
            });

            return Sha256(canonical);
        }

        private static string Sha256(string input)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }
    }

}
