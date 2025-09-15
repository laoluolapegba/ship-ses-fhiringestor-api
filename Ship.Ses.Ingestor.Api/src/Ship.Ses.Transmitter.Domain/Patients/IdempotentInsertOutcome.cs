using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Domain.Patients
{
    public enum IdempotentInsertOutcome
    {
        Inserted,                    // first time we see this (client, facility, correlationId)
        ReattemptChangedPayload,     // same key, payload changed → accept as re-attempt
        IdempotentRepeatSamePayload  // same key, same payload → reject with 409 (your policy)
    }

    


}
