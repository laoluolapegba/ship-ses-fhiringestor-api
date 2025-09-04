using Ship.Ses.Transmitter.Application.DTOs;
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
    }
}
