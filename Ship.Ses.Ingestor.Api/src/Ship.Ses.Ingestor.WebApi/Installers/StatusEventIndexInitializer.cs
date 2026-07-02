using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Ship.Ses.Ingestor.Domain.SyncModels;
using Ship.Ses.Ingestor.Infrastructure.Settings;

namespace Ship.Ses.Ingestor.WebApi.Installers
{
    /// <summary>
    /// Best-effort, idempotent ensure of the partial unique index on <c>fhirstatusevents.transactionId</c>.
    /// <para>
    /// The Transmitter OWNS this index and creates it at its own startup; this ensure mirrors the exact
    /// name and partial spec so, when both run, it is a no-op. It exists so the Ingestor's upsert-by-
    /// transactionId still enforces the single-document invariant even if the Ingestor happens to start
    /// first. It never fails startup: conflicts (an already-present compatible index) and connectivity
    /// problems are logged and swallowed, and the attempt is time-boxed so a missing Mongo cannot stall boot.
    /// </para>
    /// </summary>
    public static class StatusEventIndexInitializer
    {
        // Must match the Transmitter exactly.
        public const string IndexName = "ux_fhirstatusevents_transactionId";
        private const string CollectionName = "fhirstatusevents";

        public static async Task<WebApplication> EnsureStatusEventIndexesAsync(this WebApplication app)
        {
            var log = app.Logger;

            try
            {
                var client = app.Services.GetRequiredService<IMongoClient>();
                var settings = app.Services.GetRequiredService<IOptions<SourceDbSettings>>().Value;
                var collection = client.GetDatabase(settings.DatabaseName)
                                       .GetCollection<StatusEvent>(CollectionName);

                var keys = Builders<StatusEvent>.IndexKeys.Ascending(x => x.TransactionId);

                // Partial filter { transactionId: { $gt: "" } } — unique only over non-empty transactionIds.
                var options = new CreateIndexOptions<StatusEvent>
                {
                    Name = IndexName,
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("transactionId", new BsonDocument("$gt", ""))
                };

                // Time-box so an unreachable Mongo cannot stall startup.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await collection.Indexes.CreateOneAsync(
                    new CreateIndexModel<StatusEvent>(keys, options), cancellationToken: cts.Token);

                log.LogInformation(
                    "Ensured partial unique index {IndexName} on {Collection} (transactionId).", IndexName, CollectionName);
            }
            catch (MongoCommandException ex)
                when (ex.CodeName is "IndexOptionsConflict" or "IndexKeySpecsConflict")
            {
                // The Transmitter (or a prior run) already created a compatible index — leave it in place.
                log.LogInformation(
                    "Index {IndexName} on {Collection} already present with a compatible spec; leaving it in place.",
                    IndexName, CollectionName);
            }
            catch (Exception ex)
            {
                // Never fail startup on index ensure — the Transmitter owns this index and will create it.
                log.LogWarning(ex,
                    "Could not ensure index {IndexName} on {Collection} at startup; continuing (Transmitter owns this index).",
                    IndexName, CollectionName);
            }

            return app;
        }
    }
}
