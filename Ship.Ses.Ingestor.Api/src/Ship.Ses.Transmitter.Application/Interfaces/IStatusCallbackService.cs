using Ship.Ses.Transmitter.Application.DTOs;
using Ship.Ses.Transmitter.Domain.SyncModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Application.Interfaces
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
