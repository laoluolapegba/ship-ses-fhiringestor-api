using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Ship.Ses.Ingestor.Domain.Patients;
using System.Text.Json.Nodes;
using Swashbuckle.AspNetCore.Filters;

namespace Ship.Ses.Ingestor.WebApi.Filters
{
    public class FhirIngestRequestExample : IExamplesProvider<FhirIngestRequest>
    {
        public FhirIngestRequest GetExamples()
        {
            return new FhirIngestRequest
            {
                ResourceType = "Patient",
                ResourceId = "123",
                FhirJson = JsonNode.Parse(@"{
              ""resourceType"": ""Patient"",
              ""id"": ""123"",
              ""name"": [{ ""use"": ""official"", ""family"": ""Doe"", ""given"": [""Jane""] }],
              ""gender"": ""female"",
              ""birthDate"": ""1990-05-10""
            }")!.AsObject()
            };
        }
    }
}
