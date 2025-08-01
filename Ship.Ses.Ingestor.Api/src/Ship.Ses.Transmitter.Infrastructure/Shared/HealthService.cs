using Elasticsearch.Net;
using MassTransit.Futures.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Mysqlx.Session;
using Ship.Ses.Transmitter.Application.Shared;
using Ship.Ses.Transmitter.Domain;
using Ship.Ses.Transmitter.Infrastructure.Settings;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace Ship.Ses.Transmitter.Infrastructure.Shared
{
    public class HealthService : IHealthService
    {
        private readonly ILogger<HealthService> _logger;
        private readonly IConfiguration _configuration;
        public HealthService(
        ILogger<HealthService> logger,
        IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            var connString = _configuration.GetValue<string>("SourceDbSettings:ConnectionString");
            var dbName = _configuration.GetValue<string>("SourceDbSettings:DatabaseName");

            try
            {
                var client = new MongoClient(connString);
                var database = client.GetDatabase(dbName);

                var result = await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);

                if (result.Contains("ok") && result["ok"] == 1)
                {
                    return new HealthResult
                    {
                        Status = HealthStatus.Healthy,
                        Reason = "MongoDB is reachable"
                    };
                }

                return new HealthResult
                {
                    Status = HealthStatus.Degraded,
                    Reason = "MongoDB ping returned unexpected result"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ MongoDB Health Check Failed");

                return new HealthResult
                {
                    Status = HealthStatus.Unhealthy,
                    Reason = "MongoDB connection failed"
                };
            }
        }
    }
}
