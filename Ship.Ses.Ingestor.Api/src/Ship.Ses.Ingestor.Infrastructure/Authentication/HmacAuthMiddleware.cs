using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ship.Ses.Ingestor.Infrastructure.Authentication
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Extensions;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Ship.Ses.Ingestor.Infrastructure.Settings;
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
            if (!_opt.Enabled) { await next(ctx); return; }

            var dbg = _logger.IsEnabled(LogLevel.Debug) || _logger.IsEnabled(LogLevel.Information);

            // Correlation id for tracing in logs
            var corrId = ctx.Request.Headers.TryGetValue("X-Correlation-Id", out var cid) && !string.IsNullOrWhiteSpace(cid)
                ? cid.ToString()
                : ctx.TraceIdentifier;

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = corrId,
                ["Path"] = ctx.Request.Path.Value ?? "/"
            });

            if (_opt.RequireJwtAlso && !(ctx.User.Identity?.IsAuthenticated ?? false))
            {
                if (dbg) _logger.LogDebug("HMAC: JWT missing/unauthenticated.");
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Missing or invalid bearer token.");
                return;
            }

            // Signature header
            if (!ctx.Request.Headers.TryGetValue(_opt.SignatureHeader, out var sigHdr) || string.IsNullOrWhiteSpace(sigHdr))
            {
                if (dbg) _logger.LogDebug("HMAC: Missing header {Header}", _opt.SignatureHeader);
                await WriteProblem(ctx, 401, "Unauthorized", $"Missing signature header '{_opt.SignatureHeader}'.");
                return;
            }
            if (dbg) _logger.LogDebug("HMAC: Raw signature header: {Header}", sigHdr.ToString());

            // Timestamp
            if (!ctx.Request.Headers.TryGetValue(_opt.TimestampHeader, out var tsHdr) || !long.TryParse(tsHdr.ToString(), out var ts))
            {
                if (dbg) _logger.LogDebug("HMAC: Missing/invalid {Header}", _opt.TimestampHeader);
                await WriteProblem(ctx, 400, "Bad request", $"Missing/invalid '{_opt.TimestampHeader}'.");
                return;
            }

            // Nonce
            if (!ctx.Request.Headers.TryGetValue(_opt.NonceHeader, out var nonceValues))
            {
                if (dbg) _logger.LogDebug("HMAC: Missing {Header}", _opt.NonceHeader);
                await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'.");
                return;
            }
            var nonce = nonceValues.ToString();
            if (string.IsNullOrWhiteSpace(nonce))
            {
                if (dbg) _logger.LogDebug("HMAC: Empty nonce header.");
                await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'.");
                return;
            }
            if (dbg) _logger.LogDebug("HMAC: ts={Ts} nonce={Nonce}", ts, nonce);

            // Parse signature header: kid=...;alg=...;sig=base64
            string? kid = null, headerAlgRaw = null, sigB64 = null;
            foreach (var p in sigHdr.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                if (k == "kid") kid = v;
                else if (k == "alg") headerAlgRaw = v;
                else if (k == "sig") sigB64 = v;
            }
            if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(sigB64))
            {
                if (dbg) _logger.LogDebug("HMAC: Malformed signature header (kid/sig missing).");
                await WriteProblem(ctx, 401, "Unauthorized", "Invalid signature header format.");
                return;
            }
            if (dbg) _logger.LogDebug("HMAC: parsed kid={Kid} algRaw={AlgRaw} sigLen={SigLen}", kid, headerAlgRaw ?? "(null)", sigB64.Length);

            // Configured single-client
            var configuredKid = _opt.Clients?.ClientId;
            var configuredSecret = _opt.Clients?.ClientSecret;
            if (string.IsNullOrWhiteSpace(configuredKid) || string.IsNullOrWhiteSpace(configuredSecret))
            {
                if (dbg) _logger.LogDebug("HMAC: server misconfiguration (client id/secret missing).");
                await WriteProblem(ctx, 500, "Server misconfiguration", "HMAC client credentials not configured.");
                return;
            }
            if (!string.Equals(kid, configuredKid, StringComparison.Ordinal))
            {
                if (dbg) _logger.LogDebug("HMAC: kid mismatch: received={Kid} expected={ConfiguredKid}", kid, configuredKid);
                await WriteProblem(ctx, 401, "Unauthorized", "kid does not match configured client.");
                return;
            }

            // Clock skew
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > _opt.AllowedClockSkewSeconds)
            {
                if (dbg) _logger.LogDebug("HMAC: timestamp outside window. now={Now} ts={Ts} skew={Skew}", now, ts, _opt.AllowedClockSkewSeconds);
                await WriteProblem(ctx, 401, "Unauthorized", "Signature timestamp outside allowed window.");
                return;
            }

            // Replay protection
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
                if (dbg) _logger.LogDebug("HMAC: nonce replay detected for key={Key}", nonceKey);
                await WriteProblem(ctx, 401, "Unauthorized", "Replay detected (nonce already used).");
                return;
            }

            // Read body + hash
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
            if (dbg) _logger.LogDebug("HMAC: requestLine {Method} {Path}{Query}", ctx.Request.Method.ToUpperInvariant(), ctx.Request.Path.Value ?? "/", ctx.Request.QueryString.Value);
            if (dbg) _logger.LogDebug("HMAC: bodyHashB64={BodyHash}", bodyHashB64);

            // Canonical string
            var method = ctx.Request.Method.ToUpperInvariant();
            var path = ctx.Request.Path.Value ?? "/";
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
            var stringToSign = string.Join("\n", method, path, query, bodyHashB64, ts.ToString(), nonce, kid);
            if (dbg) _logger.LogDebug("HMAC: canonicalString:\n{Canonical}", stringToSign);

            // Normalize algs
            var configuredAlgCanonical = CanonicalizeHmacName(_opt.HmacAlgo ?? "HMACSHA256");
            var headerAlgCanonical = string.IsNullOrWhiteSpace(headerAlgRaw)
                ? configuredAlgCanonical
                : CanonicalizeHmacName(headerAlgRaw);

            if (dbg) _logger.LogDebug("HMAC: alg normalized header={HeaderAlg} configured={ConfiguredAlg}", headerAlgCanonical, configuredAlgCanonical);

            if (!string.Equals(headerAlgCanonical, configuredAlgCanonical, StringComparison.OrdinalIgnoreCase))
            {
                if (dbg) _logger.LogDebug("HMAC: algorithm not allowed (header={HeaderAlg}, expected={Expected})", headerAlgCanonical, configuredAlgCanonical);
                await WriteProblem(ctx, 401, "Unauthorized", $"Algorithm not allowed. Expected '{configuredAlgCanonical}'.");
                return;
            }

            // Compute expected signature
            var key = Encoding.UTF8.GetBytes(configuredSecret);
            byte[] mac;
            using HMAC hmac = headerAlgCanonical switch
            {
                "HMACSHA512" => new HMACSHA512(key),
                "HMACSHA256" => new HMACSHA256(key),
                _ => throw new NotSupportedException($"Unsupported HMAC algorithm: {headerAlgCanonical}")
            };
            mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            var expected = Convert.ToBase64String(mac);

            // Compare (constant time). For debugging, log lengths + short prefixes only
            var ok = FixedTimeEquals(expected, sigB64!);
            if (dbg)
            {
                _logger.LogDebug("HMAC: compare expectedLen={ExpLen} recvLen={RecvLen} expectedHead={ExpHead} recvHead={RecvHead}",
                    expected.Length, sigB64.Length,
                    SafeHead(expected), SafeHead(sigB64));
            }

            if (!ok)
            {
                _logger.LogWarning("HMAC: signature mismatch for kid={Kid} path={Path}", kid, path);
                await WriteProblem(ctx, 401, "Unauthorized", "Invalid signature.");
                return;
            }

            if (dbg) _logger.LogDebug("HMAC: signature verified.");
            await next(ctx);

            // --- local helpers ---
            static string CanonicalizeHmacName(string? alg)
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

            static string SafeHead(string s)
                => s.Length <= 8 ? s : s.Substring(0, 8) + "...";
        }

        public async Task InvokeAsync1(HttpContext ctx, RequestDelegate next)
        {
            if (!_opt.Enabled)
            {
                await next(ctx);
                return;
            }

            // Require JWT first if configured
            if (_opt.RequireJwtAlso && !(ctx.User.Identity?.IsAuthenticated ?? false))
            {
                await WriteProblem(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Missing or invalid bearer token.");
                return;
            }

            // Extract headers
            if (!ctx.Request.Headers.TryGetValue(_opt.SignatureHeader, out var sigHdr) || string.IsNullOrWhiteSpace(sigHdr))
            { await WriteProblem(ctx, 401, "Unauthorized", $"Missing signature header '{_opt.SignatureHeader}'."); return; }

            if (!ctx.Request.Headers.TryGetValue(_opt.TimestampHeader, out var tsHdr) || !long.TryParse(tsHdr.ToString(), out var ts))
            { await WriteProblem(ctx, 400, "Bad request", $"Missing/invalid '{_opt.TimestampHeader}'."); return; }

            if (!ctx.Request.Headers.TryGetValue(_opt.NonceHeader, out var nonceValues))
            { await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'."); return; }

            var nonce = nonceValues.ToString();
            if (string.IsNullOrWhiteSpace(nonce))
            { await WriteProblem(ctx, 400, "Bad request", $"Missing '{_opt.NonceHeader}'."); return; }

            // Parse signature header: kid=...;alg=HMAC-SHA256|sha256;sig=base64
            var parts = sigHdr.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
            string? kid = null, headerAlgRaw = null, sigB64 = null;
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                if (k == "kid") kid = v;
                else if (k == "alg") headerAlgRaw = v;
                else if (k == "sig") sigB64 = v;
            }
            if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(sigB64))
            { await WriteProblem(ctx, 401, "Unauthorized", "Invalid signature header format."); return; }

            // Resolve configured client
            var configuredKid = _opt.Clients?.ClientId;
            var configuredSecret = _opt.Clients?.ClientSecret;
            if (string.IsNullOrWhiteSpace(configuredKid) || string.IsNullOrWhiteSpace(configuredSecret))
            { await WriteProblem(ctx, 500, "Server misconfiguration", "HMAC client credentials not configured."); return; }

            // kid must match this single-tenant ingestor
            if (!string.Equals(kid, configuredKid, StringComparison.Ordinal))
            { await WriteProblem(ctx, 401, "Unauthorized", "kid does not match configured client."); return; }

            // Clock skew window
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > _opt.AllowedClockSkewSeconds)
            { await WriteProblem(ctx, 401, "Unauthorized", "Signature timestamp outside allowed window."); return; }

            // Replay protection
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

            // Read body for hashing
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

            // Canonical string
            var method = ctx.Request.Method.ToUpperInvariant();
            var path = ctx.Request.Path.Value ?? "/";
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
            var stringToSign = string.Join("\n", method, path, query, bodyHashB64, ts.ToString(), nonce, kid);

            //  Normalize configured algo (defaults to HMACSHA256)
            var configuredAlgCanonical = CanonicalizeHmacName(_opt.HmacAlgo ?? "HMACSHA256");  // -> "HMACSHA256" | "HMACSHA512"

            //  Normalize header 'alg' if provided; if missing, assume configured
            var headerAlgCanonical = string.IsNullOrWhiteSpace(headerAlgRaw)
                ? configuredAlgCanonical
                : CanonicalizeHmacName(headerAlgRaw);

            //  Enforce header alg matches configured alg
            if (!string.Equals(headerAlgCanonical, configuredAlgCanonical, StringComparison.OrdinalIgnoreCase))
            {
                await WriteProblem(ctx, 401, "Unauthorized", $"Algorithm not allowed. Expected '{configuredAlgCanonical}'.");
                return;
            }

            // Compute HMAC with the canonical algo
            var key = Encoding.UTF8.GetBytes(configuredSecret);
            byte[] mac;
            using HMAC hmac = headerAlgCanonical switch
            {
                "HMACSHA512" => new HMACSHA512(key),
                "HMACSHA256" => new HMACSHA256(key),
                _ => throw new NotSupportedException($"Unsupported HMAC algorithm: {headerAlgCanonical}")
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

            await next(ctx);
        }
        // Accepts variants: "sha256", "SHA-256", "hmac-sha256", etc. → "HMACSHA256"
        private static string CanonicalizeHmacName(string? alg)
        {
            if (string.IsNullOrWhiteSpace(alg)) return "HMACSHA256";
            var a = alg.Trim().Replace("-", "").Replace("_", "").ToUpperInvariant(); // e.g., "SHA256", "HMACSHA256"
            return a switch
            {
                "SHA256" => "HMACSHA256",
                "HMACSHA256" => "HMACSHA256",
                "SHA512" => "HMACSHA512",
                "HMACSHA512" => "HMACSHA512",
                _ => a // fall-through (will be rejected above if not supported)
            };
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
