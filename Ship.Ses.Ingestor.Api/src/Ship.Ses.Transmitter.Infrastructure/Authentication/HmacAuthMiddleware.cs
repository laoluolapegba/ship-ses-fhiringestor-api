using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Transmitter.Infrastructure.Authentication
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Extensions;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Ship.Ses.Transmitter.Infrastructure.Settings;
    using System.Security.Cryptography;
    using System.Text;

    public sealed class HmacAuthMiddleware : IMiddleware
    {
        private readonly HmacAuthSettings _opt;
        private readonly IMemoryCache _cache;
        private readonly ILogger<HmacAuthMiddleware> _logger;

        public HmacAuthMiddleware(IOptions<HmacAuthSettings> opt, IMemoryCache cache, ILogger<HmacAuthMiddleware> logger)
        {
            _opt = opt.Value;
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

            
            if (_opt.RequireJwtAlso && !ctx.User.Identity?.IsAuthenticated == true)
            {
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Missing or invalid bearer token.");
                return;
            }

            // Extract headers
            if (!ctx.Request.Headers.TryGetValue(_opt.SignatureHeader, out var sigHdr) || string.IsNullOrWhiteSpace(sigHdr))
            { await WriteProblem(ctx, 401, "Unauthorized", $"Missing signature header '{_opt.SignatureHeader}'."); return; }

            if (!ctx.Request.Headers.TryGetValue(_opt.TimestampHeader, out var tsHdr) ||  !long.TryParse(tsHdr.ToString(), out var ts))
            {
                await WriteProblem(ctx, 400, "Bad request", $"Missing/invalid '{_opt.TimestampHeader}'.");
                return;
            }

            // Nonce
            if (!ctx.Request.Headers.TryGetValue(_opt.NonceHeader, out var nonceValues))
            {
                await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'.");
                return;
            }
            var nonce = nonceValues.ToString();
            if (string.IsNullOrWhiteSpace(nonce))
            {
                await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'.");
                return;
            }

            // Parse signature header: kid=...;alg=HMAC-SHA256;sig=base64
            var parts = sigHdr.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
            string? kid = null, alg = null, sigB64 = null;
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                if (k == "kid") kid = v;
                else if (k == "alg") alg = v;
                else if (k == "sig") sigB64 = v;
            }
            if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(sigB64))
            { await WriteProblem(ctx, 401, "Unauthorized", "Invalid signature header format."); return; }

            // Lookup shared secret
            var configuredKid = _opt.Clients?.ClientId;
            var configuredSecret = _opt.Clients?.ClientSecret;

            if (string.IsNullOrWhiteSpace(configuredKid) || string.IsNullOrWhiteSpace(configuredSecret))
            {
                await WriteProblem(ctx, 500, "Server misconfiguration", "HMAC client credentials not configured.");
                return;
            }

            // Enforce the caller’s kid matches the configured clientId for this ingestor
            if (!string.Equals(kid, configuredKid, StringComparison.Ordinal))
            {
                await WriteProblem(ctx, 401, "Unauthorized", "kid does not match configured client.");
                return;
            }

            // Use the configured secret for signature verification
            var secret = configuredSecret;

            // Check clock skew
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > _opt.AllowedClockSkewSeconds)
            { await WriteProblem(ctx, 401, "Unauthorized", "Signature timestamp outside allowed window."); return; }

            // Replay protection: nonce must be unique within window
            var nonceKey = $"hmac:{kid}:{nonce}";
            if (!_cache.TryGetValue(nonceKey, out _))
            {
                _cache.Set(nonceKey, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opt.AllowedClockSkewSeconds)
                });
            }
            else
            {
                await WriteProblem(ctx, 401, "Unauthorized", "Replay detected (nonce already used).");
                return;
            }

            // Compute body hash (raw bytes) and construct stringToSign
            ctx.Request.EnableBuffering();
            string bodyHashB64;
            using (var sha = SHA256.Create())
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms);
                var bytes = ms.ToArray();
                bodyHashB64 = Convert.ToBase64String(sha.ComputeHash(bytes));
                ctx.Request.Body.Position = 0;
            }

            // Method + Path + Query + BodyHash + Timestamp + Nonce + ClientId
            var method = ctx.Request.Method.ToUpperInvariant();
            var path = ctx.Request.Path.Value ?? "/";
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
            var stringToSign = string.Join("\n", method, path, query, bodyHashB64, ts.ToString(), nonce.ToString(), kid);

            // Compute expected signature
            var algo = (_opt.HmacAlgo ?? "HMACSHA256").ToUpperInvariant();
            var key = Encoding.UTF8.GetBytes(secret);
            var algoName = (algo ?? "HMACSHA256").ToUpperInvariant();
            byte[] mac;

            using HMAC hmac = algoName switch
            {
                "HMACSHA512" => new HMACSHA512(key),
                "HMACSHA256" => new HMACSHA256(key),
                _ => throw new NotSupportedException($"Unsupported HMAC algorithm: {algoName}")
            };

            mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            var expected = Convert.ToBase64String(mac);

            // Constant-time compare
            if (!FixedTimeEquals(expected, sigB64!))
            {
                _logger.LogWarning("Invalid HMAC for kid={Kid} path={Path}", kid, path);
                await WriteProblem(ctx, 401, "Unauthorized", "Invalid signature.");
                return;
            }

            // All good
            await next(ctx);
        }

        private static bool FixedTimeEquals(string aB64, string bB64)
        {
            var a = Encoding.UTF8.GetBytes(aB64);
            var b = Encoding.UTF8.GetBytes(bB64);
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static async Task WriteProblem(HttpContext ctx, int code, string title, string detail)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails { Status = code, Title = title, Detail = detail, Instance = ctx.Request.Path };
                await ctx.Response.WriteAsJsonAsync(problem);
            }
        }
    }

}
