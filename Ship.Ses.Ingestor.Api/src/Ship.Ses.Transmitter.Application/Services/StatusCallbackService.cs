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
            if (request.Data is null) throw new ArgumentException("Data is required for this callback.");

            // Extract from Data
            var resourceType = request.Data["resourceType"]?.GetValue<string>() ?? "Patient";
            var resourceId = request.Data["id"]?.GetValue<string>(); // may be null

            var dataCanonical = JsonSerializer.Serialize(
                request.Data,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false });

            var dataBson = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(dataCanonical);

            var payloadHash = ComputePayloadHash(
                status: request.Status,
                message: request.Message,
                shipId: request.ShipId,
                txId: request.TransactionId,
                dataCanonical: dataCanonical);

            // Idempotency & conflicts via repo (unique index on transactionId)
            var newEvent = new StatusEvent
            {
                TransactionId = request.TransactionId,
                ResourceType = resourceType,
                ResourceId = resourceId, // nullable is fine
                ShipId = request.ShipId,
                Status = request.Status,
                Message = request.Message,
                ReceivedAtUtc = (request.Timestamp ?? DateTimeOffset.UtcNow).UtcDateTime,
                Source = "SHIP",
                Headers = (requestHeaders?.Count ?? 0) > 0 ? JsonSerializer.Serialize(requestHeaders) : null,
                PayloadHash = payloadHash,
                Data = dataBson
            };

            var (persisted, duplicate, conflict) = await _repository.UpsertPatientStatusAsync(newEvent, cancellationToken);

            if (conflict)
            {
                _logger.LogWarning("Conflicting payload for transactionId {TxId}", request.TransactionId);
                throw new InvalidOperationException("Conflicting payload for the same transactionId; existing record retained.");
            }

            if (duplicate)
            {
                _logger.LogInformation("Duplicate callback for transactionId {TxId}", request.TransactionId);
            }
            else
            {
                _logger.LogInformation("Recorded status event for transactionId {TxId}", request.TransactionId);
            }

            return new PatientTransmissionStatusResponse
            {
                TransactionId = persisted.TransactionId,
                StatusRecorded = persisted.Status,
                Duplicate = duplicate,
                RecordedAt = new DateTimeOffset(persisted.ReceivedAtUtc, TimeSpan.Zero)
            };
        }

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
