using Ship.Ses.Ingestor.Application.DTOs;
using Ship.Ses.Ingestor.Domain.SyncModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Interfaces
{
    public interface IStatusCallbackService
    {
        Task<PatientTransmissionStatusResponse> ProcessStatusUpdateAsync(
            Dictionary<string, string> requestHeaders,
            PatientTransmissionStatusRequest request,
            CancellationToken cancellationToken = default);
        Task<StatusEvent?> GetByTransactionIdAsync(string transactionId, CancellationToken ct = default);
        Task<StatusEvent?> GetByCorrelationIdAsync(string correlationId, CancellationToken ct = default);

    }
}
