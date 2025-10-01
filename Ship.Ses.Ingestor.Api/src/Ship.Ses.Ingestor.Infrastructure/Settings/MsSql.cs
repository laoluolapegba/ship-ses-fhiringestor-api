namespace Ship.Ses.Ingestor.Infrastructure.Settings
{
    public record ShipServerSqlDb (string ConnectionString);
    public record MsSql(string ConnectionString);
}
