using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Ship.Ses.Ingestor.Domain.Patients;
using Ship.Ses.Ingestor.Infrastructure.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Infrastructure.Repositories
{
    /// <summary>
    /// Generic repository for interacting with MongoDB synchronization records.
    /// </summary>
    public class MongoSyncRepository : IMongoSyncRepository
    {
        private readonly IMongoDatabase _database;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoSyncRepository"/> class.
        /// </summary>
        /// <param name="settings">The database settings from configuration.</param>
        /// <param name="client">The MongoDB client.</param>
        public MongoSyncRepository(IOptions<SourceDbSettings> settings, IMongoClient client)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.Value.DatabaseName))
            {
                throw new ArgumentException("SourceDbSettings or DatabaseName is not configured for MongoSyncRepository.", nameof(settings));
            }
            _database = client.GetDatabase(settings.Value.DatabaseName);
        }


        public async Task<IdempotentInsertResult<T>> TryInsertIdempotentAsync<T>(T record) where T : FhirSyncRecord
        {
            if (record is null) throw new ArgumentNullException(nameof(record));

            var col = _database.GetCollection<T>(record.CollectionName);

            try
            {
                await col.InsertOneAsync(record);
                return new() { Outcome = IdempotentInsertOutcome.Inserted, Document = record };
            }
            catch (MongoWriteException mwx) when (mwx.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Duplicate on (clientId, facilityId, correlationId) unique index
                var existing = await col.Find(x =>
                        x.ClientId == record.ClientId &&
                        x.FacilityId == record.FacilityId &&
                        x.CorrelationId == record.CorrelationId)
                    .FirstOrDefaultAsync();

                if (existing is null) throw; // extremely rare edge: unique error but not found

                var samePayload = string.Equals(existing.PayloadHash, record.PayloadHash, StringComparison.OrdinalIgnoreCase);

                if (samePayload)
                {
                    // SAME bad payload re-submitted with same correlationId → reject (409)
                    return new()
                    {
                        Outcome = IdempotentInsertOutcome.IdempotentRepeatSamePayload,
                        Document = existing
                    };
                }

                // DIFFERENT payload with same correlationId → treat as re-attempt
                // Persist the new payload & reset processing fields atomically.
                var filter = Builders<T>.Filter.Eq(x => x.Id, existing.Id);
                var update = Builders<T>.Update
                    .Set(x => x.FhirJson, record.FhirJson)
                    .Set(x => x.PayloadHash, record.PayloadHash)
                    .Set(x => x.ResourceType, record.ResourceType)
                    .Set(x => x.ResourceId, record.ResourceId)
                    .Set(x => x.Status, "Pending")
                    .Set(x => x.RetryCount, 0)
                    .Set(x => x.LastAttemptAt, null)
                    .Set(x => x.ApiResponsePayload, null)
                    .Set(x => x.SyncedResourceId, null)
                    .Set(x => x.ClientEMRCallbackUrl, record.ClientEMRCallbackUrl);

                _ = await col.UpdateOneAsync(filter, update);

                // Re-read the updated doc (or project fields you care about)
                var updated = await col.Find(filter).FirstAsync();

                return new()
                {
                    Outcome = IdempotentInsertOutcome.ReattemptChangedPayload,
                    Document = updated
                };
            }
        }


        public async Task<IEnumerable<T>> GetPendingRecordsAsync<T>() where T : FhirSyncRecord, new()
        {
            var collectionName = new T().CollectionName;
            var collection = _database.GetCollection<T>(collectionName);

            var filter = Builders<T>.Filter.Eq(r => r.Status, "Pending"); 
            return await collection.Find(filter).ToListAsync();
        }

        public async Task AddRecordAsync<T>(T record) where T : FhirSyncRecord
        {
            var collection = _database.GetCollection<T>(record.CollectionName);
            await collection.InsertOneAsync(record);
        }

        public async Task UpdateRecordAsync<T>(T record) where T : FhirSyncRecord
        {
            var collection = _database.GetCollection<T>(record.CollectionName);
            var filter = Builders<T>.Filter.Eq(r => r.Id, record.Id);
            await collection.ReplaceOneAsync(filter, record);
        }
        public async Task<IEnumerable<T>> GetByStatusAsync<T>(string status, int skip = 0, int take = 100)
    where T : FhirSyncRecord, new()
        {
            var collection = _database.GetCollection<T>(new T().CollectionName);
            var filter = Builders<T>.Filter.Eq(r => r.Status, status);
            return await collection.Find(filter)
                .Skip(skip)
                .Limit(take)
                .ToListAsync();
        }

        public async Task BulkUpdateStatusAsync<T>(
    Dictionary<ObjectId, (string status, string message, string transactionId, string rawResponse)> updates
) where T : FhirSyncRecord, new()
        {
            var collection = _database.GetCollection<T>(new T().CollectionName);

            var models = updates.Select(kv =>
            {
                var filter = Builders<T>.Filter.Eq(r => r.Id, kv.Key.ToString());
                var update = Builders<T>.Update
                    .Set(r => r.Status, kv.Value.status)
                    .Set(r => r.ErrorMessage, kv.Value.message)
                    .Set(r => r.TimeSynced, DateTime.UtcNow)
                    .Set(r => r.TransactionId, kv.Value.transactionId)
                    .Set(r => r.ApiResponsePayload, kv.Value.rawResponse)
                    .Set(r => r.LastAttemptAt, DateTime.UtcNow);

                return new UpdateOneModel<T>(filter, update);
            });

            await collection.BulkWriteAsync(models);
        }

        public async Task<FhirSyncRecord?> GetByTransactionIdAsync(string transactionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                throw new ArgumentNullException(nameof(transactionId));

            var patientCollection = _database.GetCollection<PatientSyncRecord>(new PatientSyncRecord().CollectionName);
            var patientFilter = Builders<PatientSyncRecord>.Filter.Eq(r => r.TransactionId, transactionId);
            var patientResult = await patientCollection.Find(patientFilter).FirstOrDefaultAsync(cancellationToken);

            if (patientResult is not null)
            {
                return patientResult;
            }

            var resourceCollection = _database.GetCollection<GenericResourceSyncRecord>(new GenericResourceSyncRecord().CollectionName);
            var resourceFilter = Builders<GenericResourceSyncRecord>.Filter.Eq(r => r.TransactionId, transactionId);
            var resourceResult = await resourceCollection.Find(resourceFilter).FirstOrDefaultAsync(cancellationToken);

            return resourceResult;
        }
    }
   
}
