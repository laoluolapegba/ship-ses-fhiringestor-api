namespace Ship.Ses.Ingestor.Application.Authentication.ReLoginCustomer
{
    public sealed record RefreshUserTokenDto(string AccessToken, string RefreshToken);
}
