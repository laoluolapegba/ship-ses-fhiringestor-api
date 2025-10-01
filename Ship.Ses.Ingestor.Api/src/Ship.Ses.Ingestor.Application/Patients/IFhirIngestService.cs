using Ship.Ses.Ingestor.Domain.Patients;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Application.Patients
{
    public interface IFhirIngestService
    {
        //Task IngestAsync(FhirIngestRequest request, string clientId);
        Task<IdempotentInsertResult<FhirSyncRecord>> IngestAsyncReturningExisting(FhirIngestRequest request, string clientId);
    }
}
