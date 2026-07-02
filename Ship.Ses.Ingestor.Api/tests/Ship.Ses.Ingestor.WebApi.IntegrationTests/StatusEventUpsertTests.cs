using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Domain.SyncModels;
using Ship.Ses.Ingestor.Infrastructure.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace Ship.Ses.Ingestor.WebApi.IntegrationTests
{
    /// <summary>
    /// Drives <see cref="StatusEventRepository.UpsertStatusEventAsync"/> against a real MongoDB, with the
    /// Transmitter-owned partial unique index in place, to prove the SHIP callback converges onto the
    /// existing <c>fhirstatusevents</c> document instead of inserting a duplicate. Requires a reachable
    /// Mongo at <see cref="IngestEndToEndFactory.ConnectionString"/>; each run uses a throwaway database.
    /// </summary>
    public sealed class StatusEventUpsertTests : IAsyncLifetime
    {
        private const string CollectionName = "fhirstatusevents";
        private const string IndexName = "ux_fhirstatusevents_transactionId";

        private readonly ITestOutputHelper _out;
        private readonly string _dbName = $"ses_upsert_tests_{Guid.NewGuid():N}";
        private MongoClient _client = default!;
        private IMongoDatabase _db = default!;
        private IMongoCollection<StatusEvent> _col = default!;
        private StatusEventRepository _repo = default!;

        public StatusEventUpsertTests(ITestOutputHelper output) => _out = output;

        public async Task InitializeAsync()
        {
            await AssertMongoIsReachable();

            _client = new MongoClient(IngestEndToEndFactory.ConnectionString);
            _db = _client.GetDatabase(_dbName);
            _col = _db.GetCollection<StatusEvent>(CollectionName);

            // Simulate the Transmitter: create the partial unique index on transactionId.
            var keys = Builders<StatusEvent>.IndexKeys.Ascending(x => x.TransactionId);
            var options = new CreateIndexOptions<StatusEvent>
            {
                Name = IndexName,
                Unique = true,
                PartialFilterExpression = new BsonDocument("transactionId", new BsonDocument("$gt", ""))
            };
            await _col.Indexes.CreateOneAsync(new CreateIndexModel<StatusEvent>(keys, options));

            _repo = new StatusEventRepository(_db);
        }

        public async Task DisposeAsync()
        {
            if (_client is not null)
                await _client.DropDatabaseAsync(_dbName);
        }

        // ---- Acceptance criteria -------------------------------------------------------------

        [Fact]
        public async Task Seeded_pending_converges_to_ship_outcome_in_place_and_delivers_once()
        {
            var tx = NewTx();
            await SeedTransmitterPending(tx);

            var (_, outcome) = await _repo.UpsertStatusEventAsync(Ship(tx, "SUCCESS", message: "accepted"), CancellationToken.None);

            Assert.Equal(StatusCallbackOutcome.Updated, outcome);
            Assert.Equal(1, await Count(tx)); // exactly one document

            var doc = await Read(tx);
            Assert.NotNull(doc);
            Assert.Equal("SUCCESS", doc!.Status);
            Assert.Equal("SHIP", doc.Source);
            Assert.Equal("accepted", doc.Message);
            Assert.Equal("Pending", doc.CallbackStatus); // outbox already Pending → worker delivers once

            // Transmitter-seeded identity/routing preserved (not clobbered).
            Assert.Equal("emr-a", doc.ClientId);
            Assert.Equal("fac-1", doc.FacilityId);
            Assert.Equal("corr-1", doc.CorrelationId);
            Assert.Equal("https://emr.example/cb", doc.EmrTargetUrl);
        }

        [Fact]
        public async Task Delivered_same_outcome_enriches_but_leaves_outbox_untouched()
        {
            var tx = NewTx();
            await SeedProbeSucceededDelivered(tx, status: "SUCCESS");

            var (_, _) = await _repo.UpsertStatusEventAsync(
                Ship(tx, "SUCCESS", message: "ship-confirms", shipId: "SHIP-XYZ"), CancellationToken.None);

            Assert.Equal(1, await Count(tx));
            var doc = await Read(tx);
            Assert.NotNull(doc);

            // Authoritative fields enriched...
            Assert.Equal("SHIP", doc!.Source);
            Assert.Equal("ship-confirms", doc.Message);
            Assert.Equal("SHIP-XYZ", doc.ShipId);
            Assert.Equal("SUCCESS", doc.Status);

            // ...but delivery state is untouched → NO second EMR callback.
            Assert.Equal("Succeeded", doc.CallbackStatus);
            Assert.Equal(1, doc.CallbackAttempts);
            Assert.NotNull(doc.CallbackDeliveredAt);
        }

        [Fact]
        public async Task Delivered_differing_outcome_overwrites_status_and_rearms_exactly_one_delivery()
        {
            var tx = NewTx();
            var seededNextAttempt = DateTime.UtcNow.AddHours(-1);
            await SeedProbeSucceededDelivered(tx, status: "SUCCESS", nextAttemptAt: seededNextAttempt, lastError: "prev");

            // Probe said SUCCESS + delivered; SHIP says REJECTED → corrective re-notify.
            await _repo.UpsertStatusEventAsync(Ship(tx, "REJECTED", message: "rejected by MPI"), CancellationToken.None);

            Assert.Equal(1, await Count(tx));
            var doc = await Read(tx);
            Assert.NotNull(doc);
            Assert.Equal("REJECTED", doc!.Status);
            Assert.Equal("SHIP", doc.Source);

            // Exactly one corrective delivery armed.
            Assert.Equal("Pending", doc.CallbackStatus);
            Assert.Null(doc.CallbackLastError);
            Assert.True(doc.CallbackNextAttemptAt > seededNextAttempt, "re-arm should refresh callbackNextAttemptAt");
            Assert.Equal(1, doc.CallbackAttempts);       // attempts preserved by re-arm
            Assert.NotNull(doc.CallbackDeliveredAt);     // delivered timestamp preserved

            // Loop guard: after the worker delivers the corrective (Succeeded again), a repeat of the SAME
            // REJECTED outcome must NOT re-arm, because the stored status already equals the SHIP value.
            await _col.UpdateOneAsync(
                x => x.TransactionId == tx,
                Builders<StatusEvent>.Update.Set(x => x.CallbackStatus, "Succeeded")
                                            .Set(x => x.CallbackDeliveredAt, DateTime.UtcNow));

            await _repo.UpsertStatusEventAsync(Ship(tx, "REJECTED", message: "rejected by MPI (again)"), CancellationToken.None);

            var after = await Read(tx);
            Assert.Equal("Succeeded", after!.CallbackStatus); // no second re-arm → no delivery loop
        }

        [Fact]
        public async Task Repeated_identical_callback_is_a_delivery_no_op()
        {
            var tx = NewTx();
            var ship = Ship(tx, "SUCCESS", message: "accepted"); // reuse the SAME instance for byte-identical $set

            var first = await _repo.UpsertStatusEventAsync(ship, CancellationToken.None);
            Assert.Equal(StatusCallbackOutcome.Inserted, first.outcome);
            Assert.Equal(1, await Count(tx));

            var second = await _repo.UpsertStatusEventAsync(ship, CancellationToken.None);
            Assert.Equal(StatusCallbackOutcome.Unchanged, second.outcome);

            Assert.Equal(1, await Count(tx)); // no duplicate document
            var doc = await Read(tx);
            Assert.Equal("Pending", doc!.CallbackStatus); // no re-arm
            Assert.Equal(0, doc.CallbackAttempts);
        }

        [Fact]
        public async Task Ship_first_insert_writes_full_event_ready_for_single_delivery()
        {
            var tx = NewTx();

            var (_, outcome) = await _repo.UpsertStatusEventAsync(Ship(tx, "ERROR", message: "mpi error"), CancellationToken.None);

            Assert.Equal(StatusCallbackOutcome.Inserted, outcome);
            Assert.Equal(1, await Count(tx));
            var doc = await Read(tx);
            Assert.NotNull(doc);
            Assert.Equal("SHIP", doc!.Source);
            Assert.Equal("ERROR", doc.Status);
            Assert.Equal("Pending", doc.CallbackStatus);
            Assert.Equal(0, doc.CallbackAttempts);
            Assert.Null(doc.CallbackDeliveredAt);
            Assert.NotNull(doc.CallbackNextAttemptAt);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Missing_transaction_id_is_rejected_and_creates_no_orphan(string tx)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _repo.UpsertStatusEventAsync(Ship(tx, "SUCCESS"), CancellationToken.None));

            // No deliverable orphan event was created.
            var count = await _col.CountDocumentsAsync(FilterDefinition<StatusEvent>.Empty);
            Assert.Equal(0, count);
        }

        // ---- Helpers -------------------------------------------------------------------------

        private static string NewTx() => $"txn-{Guid.NewGuid():N}";

        private static StatusEvent Ship(string tx, string status, string message = "ok", string shipId = "SHIP-1") =>
            new StatusEvent
            {
                TransactionId = tx,
                Status = status,
                Message = message,
                ShipId = shipId,
                Source = "SHIP",
                ResourceType = "Patient",
                ResourceId = string.IsNullOrWhiteSpace(tx) ? "r-1" : tx,
                ReceivedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                PayloadHash = $"{status}|{message}|{shipId}"
            };

        private async Task SeedTransmitterPending(string tx)
        {
            await _col.InsertOneAsync(new StatusEvent
            {
                TransactionId = tx,
                Status = "PENDING",
                Source = "PROBE",
                Message = "seeded",
                ShipId = "",
                ResourceType = "Patient",
                ResourceId = tx,
                ReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                PayloadHash = "seed",
                ClientId = "emr-a",
                FacilityId = "fac-1",
                CorrelationId = "corr-1",
                EmrTargetUrl = "https://emr.example/cb",
                CallbackStatus = "Pending",
                CallbackAttempts = 0,
                CallbackNextAttemptAt = DateTime.UtcNow,
                ProbeStatus = "Pending"
            });
        }

        private async Task SeedProbeSucceededDelivered(
            string tx, string status, DateTime? nextAttemptAt = null, string? lastError = null)
        {
            await _col.InsertOneAsync(new StatusEvent
            {
                TransactionId = tx,
                Status = status,
                Source = "PROBE",
                Message = "probe-promoted",
                ShipId = "SHIP-SEED",
                ResourceType = "Patient",
                ResourceId = tx,
                ReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                PayloadHash = "probe",
                ClientId = "emr-a",
                FacilityId = "fac-1",
                CorrelationId = "corr-1",
                EmrTargetUrl = "https://emr.example/cb",
                CallbackStatus = "Succeeded",
                CallbackAttempts = 1,
                CallbackNextAttemptAt = nextAttemptAt ?? DateTime.UtcNow.AddMinutes(-9),
                CallbackDeliveredAt = DateTime.UtcNow.AddMinutes(-8),
                CallbackLastError = lastError,
                EmrResponseStatusCode = 200,
                ProbeStatus = "Succeeded"
            });
        }

        private Task<StatusEvent?> Read(string tx) =>
            _col.Find(x => x.TransactionId == tx).FirstOrDefaultAsync()!;

        private async Task<long> Count(string tx) =>
            await _col.CountDocumentsAsync(x => x.TransactionId == tx);

        private static async Task AssertMongoIsReachable()
        {
            try
            {
                var client = new MongoClient(IngestEndToEndFactory.ConnectionString);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.GetDatabase("admin")
                            .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"MongoDB is not reachable at {IngestEndToEndFactory.ConnectionString}. " +
                    "Start one (e.g. `docker run -d --name ses-mongo-test -p 27017:27017 mongo:7`) and retry.",
                    ex);
            }
        }
    }
}
