using Ship.Ses.Ingestor.Domain.Enums;

namespace Ship.Ses.Ingestor.Application.Shared
{
    public interface IEmailTemplateFactory
    {
        Task<string> GetTemplateAsync(EmailTemplateType templateType);
    }
}
