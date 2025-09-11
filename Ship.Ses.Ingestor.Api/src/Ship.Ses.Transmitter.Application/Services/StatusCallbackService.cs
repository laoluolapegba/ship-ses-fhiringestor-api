using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Ship.Ses.Transmitter.Application.DTOs;
using Ship.Ses.Transmitter.Application.Interfaces;
using Ship.Ses.Transmitter.Domain.Events;
using Ship.Ses.Transmitter.Domain.SyncModels;
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

namespace Ship.Ses.Transmitter.Application.Services
{
    public class StatusCallbackService : IStatusCallbackService
    {
        private readonly ILogger<StatusCallbackService> _logger;
        private readonly IStatusEventRepository _repository;

        public StatusCallbackService(ILogger<StatusCallbackService> logger, IStatusEventRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        public async Task<PatientTransmissionStatusResponse> ProcessStatusUpdateAsync(
    Dictionary<string, string> requestHeaders,
    PatientTransmissionStatusRequest request,
    CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentException("Request cannot be null.");
            if (string.IsNullOrWhiteSpace(request.TransactionId)) throw new ArgumentException("TransactionId is required.");

            _logger.LogInformation("Processing status update for transactionId: {TransactionId}", request.TransactionId);

            // Optional hints via headers (caller can pass these if they want)
            const string HeaderResourceType = "x-fhir-resource-type";
            const string HeaderResourceId = "x-fhir-resource-id";

            var resourceType = TryGetHeader(requestHeaders, HeaderResourceType) ?? "Patient";
            var resourceId = TryGetHeader(requestHeaders, HeaderResourceId);

            if (resourceId is null)
                _logger.LogDebug("No resource ID supplied in headers for transactionId {TxId}.", request.TransactionId);

            _logger.LogInformation(
                "Using resourceType '{ResourceType}' and resourceId '{ResourceId}' for transactionId {TxId}.",
                resourceType, resourceId, request.TransactionId
            );

            // No request.Data anymore. We compute a hash over the stable fields.
            string? dataCanonical = null; // intentionally null since no payload body beyond the DTO
            var payloadHash = ComputePayloadHash(
                status: request.Status,
                message: request.Message,
                shipId: request.ShipId,
                txId: request.TransactionId,
                dataCanonical: dataCanonical // null → dataSha256 omitted in canonical
            );

            _logger.LogDebug("Computed payload hash: {PayloadHash}", payloadHash);

            var newEvent = new StatusEvent
            {
                TransactionId = request.TransactionId,
                ResourceType = resourceType,
                ResourceId = resourceId,
                ShipId = request.ShipId,
                Status = request.Status,
                Message = request.Message,
                ReceivedAtUtc = (request.Timestamp ?? DateTimeOffset.UtcNow).UtcDateTime,
                Source = "SHIP",
                Headers = (requestHeaders?.Count ?? 0) > 0 ? JsonSerializer.Serialize(requestHeaders) : null,
                PayloadHash = payloadHash,
                Data = null // <-- important: no BSON payload now that Data is removed
            };

            var (persisted, duplicate, conflict) = await _repository.UpsertPatientStatusAsync(newEvent, cancellationToken);

            if (conflict)
            {
                _logger.LogError("Conflict detected for transactionId {TxId}. The incoming payload conflicts with an existing record.", request.TransactionId);
                throw new InvalidOperationException("Conflicting payload for the same transactionId; existing record retained.");
            }
            else if (duplicate)
            {
                _logger.LogInformation("Duplicate callback received for transactionId {TxId}. No new record was created.", request.TransactionId);
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
        private static string ComputePayloadHash(string status, string message, string shipId, string txId, string dataCanonical)
        {
            static string Sha256(string s)
            {
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                return Convert.ToHexString(bytes);
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
