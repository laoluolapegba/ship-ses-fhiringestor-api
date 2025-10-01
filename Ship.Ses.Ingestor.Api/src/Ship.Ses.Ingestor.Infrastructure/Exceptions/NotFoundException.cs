namespace Ship.Ses.Ingestor.Infrastructure.Exceptions
{
    public class NotFoundException : InfrastructureException
    {
        public NotFoundException(Guid id) : base($"Object of id: {id} not found.")
        {
        }
    }
}
