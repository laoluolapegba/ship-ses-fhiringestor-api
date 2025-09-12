using Ship.Ses.Transmitter.Domain.SyncModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Application.Interfaces
{
    public interface IStatusEventRepository
    {
        //Task<StatusEvent> FindByTransactionIdAsync(string transactionId);
        //Task AddAsync(StatusEvent statusEvent); 

        Task<(StatusEvent persisted, bool duplicate, bool conflict)>
        UpsertPatientStatusAsync(StatusEvent incoming, CancellationToken ct);
        Task<StatusEvent?> GetByTransactionIdAsync(string transactionId, CancellationToken ct);
        Task<StatusEvent?> GetByCorrelationIdAsync(string transactionId, CancellationToken ct);
    }
}

