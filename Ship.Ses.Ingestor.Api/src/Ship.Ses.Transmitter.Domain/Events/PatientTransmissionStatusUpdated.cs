using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Domain.Events
{
    public class PatientTransmissionStatusUpdated
    {
        public string TransactionId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string ResourceId { get; set; }
        public string ShipId { get; set; }
    }
}
