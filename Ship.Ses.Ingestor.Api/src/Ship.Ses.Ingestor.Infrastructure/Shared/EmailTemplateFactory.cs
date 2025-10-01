using Ship.Ses.Ingestor.Application.Shared;
using Ship.Ses.Ingestor.Domain.Enums;
using Ship.Ses.Ingestor.Infrastructure.Exceptions;

namespace Ship.Ses.Ingestor.Infrastructure.Shared
{
    public class EmailTemplateFactory : IEmailTemplateFactory
    {
        private readonly string _templateDirectory = "EmailTemplates";

        public async Task<string> GetTemplateAsync(EmailTemplateType templateType)
        {
            var fileName = $"{templateType}.html";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "Ship.Ses.Ingestor.Infrastructure", "EmailTemplates", fileName);

            if (!File.Exists(filePath))
            {
                throw new InfrastructureException($"Template '{fileName}' not found in '{_templateDirectory}'.");
            }

            return await File.ReadAllTextAsync(filePath);
        }

    }

}
