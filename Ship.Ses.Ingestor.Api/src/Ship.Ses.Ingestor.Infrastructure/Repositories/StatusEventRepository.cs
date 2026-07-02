using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Domain.SyncModels;
using Ship.Ses.Ingestor.Infrastructure.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Infrastructure.Repositories
{
    public sealed class StatusEventRepository : IStatusEventRepository
    {
        private readonly IMongoCollection<StatusEvent> _col;
        public StatusEventRepository(IMongoDatabase db) => _col = db.GetCollection<StatusEvent>("fhirstatusevents");
        private readonly IMongoDatabase _database;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoSyncRepository"/> class.
        /// </summary>
        /// <param name="settings">The database settings from configuration.</param>
        /// <param name="client">The MongoDB client.</param>
        public StatusEventRepository(IOptions<SourceDbSettings> settings, IMongoClient client)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.Value.DatabaseName))
            {
                throw new ArgumentException("SourceDbSettings or DatabaseName is not configured for MongoSyncRepository.", nameof(settings));
            }
            _database = client.GetDatabase(settings.Value.DatabaseName);
            _col = _database.GetCollection<StatusEvent>("fhirstatusevents");
        }
        /// <summary>
        /// Correlates a SHIP callback onto the shared <c>fhirstatusevents</c> document via a single
        /// atomic aggregation-pipeline upsert keyed on <c>transactionId</c>. Never blind-inserts.
        /// <para>
        /// SHIP is authoritative over PROBE. The authoritative fields (<c>source</c>, <c>status</c>,
        /// <c>message</c>, <c>shipId</c>, <c>receivedAtUtc</c>, <c>payloadHash</c>, <c>headers</c>,
        /// <c>data</c>) are always written; identity/routing fields are filled only when missing so a
        /// Transmitter seed is never clobbered. The EMR outbox is preserved except:
        /// on a fresh insert it starts at Pending/now; and when a SHIP status differs from an
        /// already-delivered stored status (<c>callbackStatus == "Succeeded"</c>) exactly one corrective
        /// delivery is re-armed. Because the stored status is overwritten to the SHIP value in the same
        /// operation, a later repeat of that corrected outcome compares equal and does not re-arm.
        /// </para>
        /// </summary>
        public async Task<(StatusEvent persisted, StatusCallbackOutcome outcome)> UpsertStatusEventAsync(StatusEvent incoming, CancellationToken ct)
        {
            if (incoming is null) throw new ArgumentNullException(nameof(incoming));
            if (string.IsNullOrWhiteSpace(incoming.TransactionId))
                throw new ArgumentException("transactionId is required to correlate a status callback.", nameof(incoming));

            var filter = Builders<StatusEvent>.Filter.Eq(x => x.TransactionId, incoming.TransactionId);
            var update = Builders<StatusEvent>.Update.Pipeline(BuildUpsertPipeline(incoming));

            // The unique partial index on transactionId makes concurrent upserts of a not-yet-seeded txn
            // safe: one wins the insert, the loser gets a duplicate-key error and retries as a plain
            // in-place update (the document now exists), so at most one document ever exists per txn.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var result = await _col.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

                    StatusCallbackOutcome outcome;
                    if (result.UpsertedId is not null)
                        outcome = StatusCallbackOutcome.Inserted;
                    else if (result.IsModifiedCountAvailable && result.ModifiedCount > 0)
                        outcome = StatusCallbackOutcome.Updated;
                    else
                        outcome = StatusCallbackOutcome.Unchanged;

                    // The response only needs transactionId/status/receivedAtUtc, all written unconditionally,
                    // so the incoming projection already reflects the persisted authoritative values.
                    return (incoming, outcome);
                }
                catch (MongoWriteException ex)
                    when (attempt == 0 && ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // Lost the insert race; the document now exists — retry as an in-place update.
                }
            }
        }

        /// <summary>
        /// Builds the aggregation-pipeline <c>$set</c> stage implementing the enrich + conditional
        /// outbox re-arm policy in one server-side operation. Field references (<c>$status</c>,
        /// <c>$callbackStatus</c>) read the pre-update document; on an upsert-insert they are missing.
        /// </summary>
        private static PipelineDefinition<StatusEvent, StatusEvent> BuildUpsertPipeline(StatusEvent incoming)
        {
            // $literal keeps caller-supplied values (which may contain '$' or look like field paths)
            // from being interpreted as aggregation expressions.
            static BsonValue Lit(string? s) => new BsonDocument("$literal", s is null ? BsonNull.Value : new BsonString(s));
            static BsonDocument Cond(BsonValue ifExpr, BsonValue then, BsonValue @else) =>
                new BsonDocument("$cond", new BsonArray { ifExpr, then, @else });
            static BsonValue IfNull(string field, string? incomingValue) =>
                new BsonDocument("$ifNull", new BsonArray { "$" + field, Lit(incomingValue) });

            // On an upsert-insert callbackStatus is absent; on any existing document it is present.
            var isInsert = new BsonDocument("$eq",
                new BsonArray { new BsonDocument("$type", "$callbackStatus"), "missing" });

            // The SHIP status differs from what is stored AND the event was already delivered.
            var reArm = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { "$status", Lit(incoming.Status) }),
                new BsonDocument("$eq", new BsonArray { "$callbackStatus", "Succeeded" })
            });

            BsonValue receivedAt = new BsonDateTime(DateTime.SpecifyKind(incoming.ReceivedAtUtc, DateTimeKind.Utc));
            BsonValue data = incoming.Data is null
                ? new BsonDocument("$literal", BsonNull.Value)
                : new BsonDocument("$literal", incoming.Data);

            var set = new BsonDocument
            {
                // Correlation key (redundant with the filter on insert; identical on update).
                { "transactionId", Lit(incoming.TransactionId) },

                // Authoritative SHIP fields — always overwritten.
                { "source", "SHIP" },
                { "status", Lit(incoming.Status) },
                { "message", Lit(incoming.Message) },
                { "shipId", Lit(incoming.ShipId) },
                { "receivedAtUtc", receivedAt },
                { "payloadHash", Lit(incoming.PayloadHash) },
                { "headers", Lit(incoming.Headers) },
                { "data", data },

                // Identity / routing — fill only when the stored document lacks them (never clobber a seed).
                { "correlationId", IfNull("correlationId", incoming.CorrelationId) },
                { "clientId", IfNull("clientId", incoming.ClientId) },
                { "facilityId", IfNull("facilityId", incoming.FacilityId) },
                { "resourceType", IfNull("resourceType", incoming.ResourceType) },
                { "resourceId", IfNull("resourceId", incoming.ResourceId) },
                { "shipService", IfNull("shipService", incoming.ShipService) },
                { "emrTargetUrl", IfNull("emrTargetUrl", incoming.EmrTargetUrl) },

                // EMR outbox — default on insert, re-arm exactly one corrective delivery, else preserve.
                { "callbackStatus", Cond(isInsert, "Pending", Cond(reArm, "Pending", "$callbackStatus")) },
                { "callbackNextAttemptAt", Cond(isInsert, "$$NOW", Cond(reArm, "$$NOW", "$callbackNextAttemptAt")) },
                { "callbackLastError", Cond(isInsert, BsonNull.Value, Cond(reArm, BsonNull.Value, "$callbackLastError")) },
                { "callbackAttempts", Cond(isInsert, 0, "$callbackAttempts") },
                { "callbackDeliveredAt", Cond(isInsert, BsonNull.Value, "$callbackDeliveredAt") }
            };

            PipelineDefinition<StatusEvent, StatusEvent> pipeline = new BsonDocument[]
            {
                new BsonDocument("$set", set)
            };
            return pipeline;
        }

        /// <summary>
        /// Retrieves a single document by transactionId from the collection associated with T.
        /// Uses the BSON element name "transactionId" to avoid strong typing constraints.
        /// </summary>
        public async Task<T?> GetByTransactionIdAsync<T>(string transactionId)
            where T : BaseMongoDocument, new()
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                throw new ArgumentException("transactionId is required.", nameof(transactionId));

            var collection = _database.GetCollection<T>(new T().CollectionName);

            // Match the BSON element name used in models: [BsonElement("transactionId")]
            var filter = Builders<T>.Filter.Eq("transactionId", transactionId);

            return await collection.Find(filter).FirstOrDefaultAsync();
        }
        public Task<StatusEvent?> GetByTransactionIdAsync(string txId, CancellationToken ct) =>
            _col.Find(x => x.TransactionId == txId).FirstOrDefaultAsync(ct);

        public Task<StatusEvent?> GetByCorrelationIdAsync(string corId, CancellationToken ct) =>
            _col.Find(x => x.CorrelationId == corId).FirstOrDefaultAsync(ct);

        public async Task<T?> GetBySyncedResourceIdAsync<T>(string txId)
    where T : FhirSyncRecord, new()
        {
            var col = _database.GetCollection<T>(new T().CollectionName);
            var filter = Builders<T>.Filter.Eq("syncedFhirResourceId", txId);
            return await col.Find(filter).FirstOrDefaultAsync();
        }
    }
    //public class StatusEventRepository : IStatusEventRepository
    //{
    //    private readonly IMongoCollection<StatusEvent> _statusEvents;

    //    public StatusEventRepository(IMongoClient client, string databaseName)
    //    {
    //        var database = client.GetDatabase(databaseName);
    //        _statusEvents = database.GetCollection<StatusEvent>("fhirstatusevents");
    //    }

    //    public async Task<StatusEvent> FindByTransactionIdAsync(string transactionId)
    //    {
    //        var filter = Builders<StatusEvent>.Filter.Eq(e => e.TransactionId, transactionId);
    //        return await _statusEvents.Find(filter).FirstOrDefaultAsync();
    //    }

    //    public async Task AddAsync(StatusEvent statusEvent)
    //    {
    //        await _statusEvents.InsertOneAsync(statusEvent);
    //    }
    //}
}
 