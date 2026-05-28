using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ship.Ses.Ingestor.Application.Interfaces;
using Ship.Ses.Ingestor.Infrastructure.Settings;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    public sealed class HmacAuthMiddleware : IMiddleware
    {
        private readonly HmacAuthSettings _opt;
        private readonly IClientCredentialResolver _credentialResolver;
        private readonly IMemoryCache _cache;
        private readonly ILogger<HmacAuthMiddleware> _logger;

        public HmacAuthMiddleware(
            IOptions<HmacAuthSettings> opt,
            IClientCredentialResolver credentialResolver,
            IMemoryCache cache,
            ILogger<HmacAuthMiddleware> logger)
        {
            _opt = opt.Value;
            _credentialResolver = credentialResolver;
            _cache = cache;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
        {
            if (!_opt.Enabled)
            {
                await next(ctx);
                return;
            }

            var ingestMetadata = await ReadIngestMetadataAsync(ctx.Request, ctx.RequestAborted);
            var rawClientId = ctx.User.FindFirst("client_id")?.Value ?? ctx.User.FindFirst("azp")?.Value;
            var corrId = !string.IsNullOrWhiteSpace(ingestMetadata.CorrelationId)
                ? ingestMetadata.CorrelationId
                : ctx.Request.Headers.TryGetValue("X-Correlation-Id", out var cid) && !string.IsNullOrWhiteSpace(cid)
                    ? cid.ToString()
                    : ctx.TraceIdentifier;

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = corrId,
                ["ClientId"] = rawClientId,
                ["FacilityId"] = ingestMetadata.FacilityId,
                ["ShipService"] = ingestMetadata.ShipService,
                ["Path"] = ctx.Request.Path.Value ?? "/"
            });

            if (_opt.RequireJwtAlso && !(ctx.User.Identity?.IsAuthenticated ?? false))
            {
                _logger.LogWarning("HMAC validation failed: JWT missing or unauthenticated.");
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Missing or invalid bearer token.");
                return;
            }

            if (string.IsNullOrWhiteSpace(rawClientId))
            {
                _logger.LogWarning("HMAC client resolution failed: missing client_id/azp claim for facility {FacilityId} service {ShipService}.",
                    ingestMetadata.FacilityId, ingestMetadata.ShipService);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Missing 'client_id' or 'azp' claim.");
                return;
            }

            if (!ctx.Request.Headers.TryGetValue(_opt.SignatureHeader, out var sigHdr) || string.IsNullOrWhiteSpace(sigHdr))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: missing signature header {Header}.",
                    rawClientId, _opt.SignatureHeader);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", $"Missing signature header '{_opt.SignatureHeader}'.");
                return;
            }

            if (!ctx.Request.Headers.TryGetValue(_opt.TimestampHeader, out var tsHdr) || !long.TryParse(tsHdr.ToString(), out var ts))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: missing or invalid timestamp header {Header}.",
                    rawClientId, _opt.TimestampHeader);
                await WriteProblem(ctx, StatusCodes.Status400BadRequest, "Bad request", $"Missing/invalid '{_opt.TimestampHeader}'.");
                return;
            }

            if (!ctx.Request.Headers.TryGetValue(_opt.NonceHeader, out var nonceValues) || string.IsNullOrWhiteSpace(nonceValues.ToString()))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: missing nonce header {Header}.",
                    rawClientId, _opt.NonceHeader);
                await WriteProblem(ctx, StatusCodes.Status400BadRequest, "Bad request", $"Missing '{_opt.NonceHeader}'.");
                return;
            }

            var nonce = nonceValues.ToString();
            var (kid, headerAlgRaw, sigB64) = ParseSignatureHeader(sigHdr.ToString());
            if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(sigB64))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: malformed signature header.", rawClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Invalid signature header format.");
                return;
            }

            if (!string.Equals(kid, rawClientId, StringComparison.Ordinal))
            {
                _logger.LogWarning("HMAC validation failed: signature kid does not match authenticated client {ClientId}.",
                    rawClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Signature client does not match authenticated client.");
                return;
            }

            var credential = await _credentialResolver.ResolveByClientIdAsync(rawClientId, ctx.RequestAborted);
            if (credential is null)
            {
                _logger.LogWarning("HMAC client resolution failed: unknown ClientId {ClientId}.", rawClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Unknown client.");
                return;
            }

            if (!credential.IsActive || credential.IsRevoked)
            {
                _logger.LogWarning("HMAC client resolution failed: inactive or revoked ClientId {ClientId}.", credential.ClientId);
                await WriteProblem(ctx, StatusCodes.Status403Forbidden, "Forbidden", "Client is inactive or revoked.");
                return;
            }

            if (string.IsNullOrWhiteSpace(credential.ClientSecret))
            {
                _logger.LogWarning("HMAC client resolution failed: secret unavailable for ClientId {ClientId}.", credential.ClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Client secret is unavailable.");
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > _opt.AllowedClockSkewSeconds)
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: timestamp outside allowed window.", credential.ClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Signature timestamp outside allowed window.");
                return;
            }

            var nonceKey = $"hmac:{credential.ClientId}:{nonce}";
            if (_cache.TryGetValue(nonceKey, out _))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: nonce replay detected.", credential.ClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Replay detected (nonce already used).");
                return;
            }

            _cache.Set(nonceKey, true, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opt.AllowedClockSkewSeconds)
            });

            var bodyHashB64 = await ComputeBodyHashAsync(ctx.Request, ctx.RequestAborted);
            var method = ctx.Request.Method.ToUpperInvariant();
            var path = ctx.Request.Path.Value ?? "/";
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
            var stringToSign = string.Join("\n", method, path, query, bodyHashB64, ts.ToString(), nonce, kid);

            var configuredAlgCanonical = CanonicalizeHmacName(_opt.HmacAlgo);
            var headerAlgCanonical = string.IsNullOrWhiteSpace(headerAlgRaw)
                ? configuredAlgCanonical
                : CanonicalizeHmacName(headerAlgRaw);

            if (!string.Equals(headerAlgCanonical, configuredAlgCanonical, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: algorithm {HeaderAlg} is not allowed.",
                    credential.ClientId, headerAlgCanonical);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", $"Algorithm not allowed. Expected '{configuredAlgCanonical}'.");
                return;
            }

            if (credential.AllowedAlgorithms.Count > 0 &&
                !credential.AllowedAlgorithms.Select(CanonicalizeHmacName).Contains(headerAlgCanonical, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: algorithm {HeaderAlg} is not allowed by registry.",
                    credential.ClientId, headerAlgCanonical);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Algorithm not allowed for client.");
                return;
            }

            var expected = ComputeSignature(credential.ClientSecret, headerAlgCanonical, stringToSign);
            if (!FixedTimeEquals(expected, sigB64))
            {
                _logger.LogWarning("HMAC validation failed for client {ClientId}: invalid signature.", credential.ClientId);
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Invalid signature.");
                return;
            }

            _logger.LogDebug("HMAC signature verified for client {ClientId}.", credential.ClientId);
            await next(ctx);
        }

        private static async Task<IngestMetadata> ReadIngestMetadataAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            request.EnableBuffering();

            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                var root = doc.RootElement;

                return new IngestMetadata(
                    ReadString(root, "facilityId"),
                    ReadString(root, "shipService"),
                    ReadString(root, "correlationId"));
            }
            catch (JsonException)
            {
                return new IngestMetadata(null, null, null);
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        private static async Task<string> ComputeBodyHashAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            request.EnableBuffering();
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, cancellationToken);
            request.Body.Position = 0;
            return Convert.ToBase64String(sha.ComputeHash(ms.ToArray()));
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }

        private static (string? Kid, string? Algorithm, string? Signature) ParseSignatureHeader(string header)
        {
            string? kid = null;
            string? algorithm = null;
            string? signature = null;

            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                var key = kv[0].Trim().ToLowerInvariant();
                var value = kv[1].Trim();
                if (key == "kid") kid = value;
                else if (key == "alg") algorithm = value;
                else if (key == "sig") signature = value;
            }

            return (kid, algorithm, signature);
        }

        private static string ComputeSignature(string secret, string algorithm, string stringToSign)
        {
            var key = Encoding.UTF8.GetBytes(secret);
            using HMAC hmac = algorithm switch
            {
                "HMACSHA512" => new HMACSHA512(key),
                "HMACSHA256" => new HMACSHA256(key),
                _ => throw new NotSupportedException($"Unsupported HMAC algorithm: {algorithm}")
            };

            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        }

        private static string CanonicalizeHmacName(string? alg)
        {
            if (string.IsNullOrWhiteSpace(alg)) return "HMACSHA256";
            var a = alg.Trim().Replace("-", "").Replace("_", "").ToUpperInvariant();
            return a switch
            {
                "SHA256" => "HMACSHA256",
                "HMACSHA256" => "HMACSHA256",
                "SHA512" => "HMACSHA512",
                "HMACSHA512" => "HMACSHA512",
                _ => a
            };
        }

        private static bool FixedTimeEquals(string aB64, string bB64)
        {
            var a = Encoding.UTF8.GetBytes(aB64);
            var b = Encoding.UTF8.GetBytes(bB64);
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static async Task WriteProblem(HttpContext ctx, int code, string title, string detail)
        {
            if (ctx.Response.HasStarted)
            {
                return;
            }

            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails { Status = code, Title = title, Detail = detail, Instance = ctx.Request.Path };
            await ctx.Response.WriteAsJsonAsync(problem);
        }

        private sealed record IngestMetadata(string? FacilityId, string? ShipService, string? CorrelationId);
    }
}
