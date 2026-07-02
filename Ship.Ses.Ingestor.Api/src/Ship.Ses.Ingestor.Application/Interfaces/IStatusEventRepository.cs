using Ship.Ses.Ingestor.Domain.SyncModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Interfaces
{
    /// <summary>
    /// Result of correlating a SHIP callback onto the shared <c>fhirstatusevents</c> document.
    /// </summary>
    public enum StatusCallbackOutcome
    {
        /// <summary>No event existed for the transactionId; a SHIP-first document was inserted.</summary>
        Inserted,

        /// <summary>An existing event (Transmitter seed or probe result) was enriched/converged in place.</summary>
        Updated,

        /// <summary>An existing event already reflected this callback; nothing changed (idempotent no-op).</summary>
        Unchanged
    }

    public interface IStatusEventRepository
    {
        /// <summary>
        /// Correlates the SHIP callback to the existing <c>fhirstatusevents</c> document by
        /// <c>transactionId</c> and applies it via a single atomic upsert. Never blind-inserts:
        /// SHIP is authoritative over PROBE, delivery/outbox state is preserved, and exactly one
        /// corrective EMR delivery is re-armed only when a delivered outcome is overwritten.
        /// </summary>
        Task<(StatusEvent persisted, StatusCallbackOutcome outcome)>
        UpsertStatusEventAsync(StatusEvent incoming, CancellationToken ct);
        Task<StatusEvent?> GetByTransactionIdAsync(string transactionId, CancellationToken ct);
        Task<StatusEvent?> GetByCorrelationIdAsync(string transactionId, CancellationToken ct);
    }
}
