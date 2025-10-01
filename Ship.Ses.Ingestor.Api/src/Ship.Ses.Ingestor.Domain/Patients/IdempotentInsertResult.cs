using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Domain.Patients
{
    public sealed class IdempotentInsertResult<T>
    {
        public IdempotentInsertOutcome Outcome { get; init; }
        public T Document { get; init; } = default!;
    }
}
