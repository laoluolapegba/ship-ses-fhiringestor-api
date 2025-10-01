namespace Ship.Ses.Ingestor.Infrastructure.Settings
{
    public record Smtp(string Server, int Port, string User, string Password, bool EnableSsl, string EmailFrom);

}
