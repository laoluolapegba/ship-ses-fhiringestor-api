using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ship.Ses.Transmitter.Application.Interfaces;
using Ship.Ses.Transmitter.Domain.Patients;
using Ship.Ses.Transmitter.Domain.SyncModels;
using Ship.Ses.Transmitter.Infrastructure.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Infrastructure.Persistance.Configuration.Domain
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
        public async Task<(StatusEvent persisted, bool duplicate, bool conflict)>  UpsertStatusEventAsync(StatusEvent incoming, CancellationToken ct)
        {
            try
            {
                await _col.InsertOneAsync(incoming, cancellationToken: ct);
                return (incoming, duplicate: false, conflict: false);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var existing = await _col.Find(x => x.TransactionId == incoming.TransactionId)
                                         .FirstOrDefaultAsync(ct);

                if (existing is null) return (incoming, false, conflict: true);

                var same = existing.PayloadHash == incoming.PayloadHash
                           && existing.Status == incoming.Status
                           && existing.ShipId == incoming.ShipId;

                return (existing, duplicate: same, conflict: !same);
            }
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
 