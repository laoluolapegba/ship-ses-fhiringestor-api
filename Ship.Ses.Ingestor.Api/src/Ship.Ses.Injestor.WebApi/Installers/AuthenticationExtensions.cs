namespace Ship.Ses.Transmitter.WebApi.Installers
{
    // Ship.Ses.Transmitter.WebApi/Extensions/AuthenticationExtensions.cs

    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.IdentityModel.JsonWebTokens;
    using Microsoft.IdentityModel.Tokens;
    using Microsoft.OpenApi.Models; // For Swagger
    using Swashbuckle.AspNetCore.SwaggerGen; // For SwaggerGenOptions
    using System.IdentityModel.Tokens.Jwt;
    using System.Text;
    using System.Text.Json;

    public static class AuthenticationExtensions
    {
        public static IServiceCollection AddIDPAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection("AppSettings:Authentication");
            var authority = section["Authority"]?.TrimEnd('/');
            var audience = section["Audience"];                 // Prefer your API client-id in Keycloak
            var acceptAzp = bool.TryParse(section["AcceptAzpAsAudience"], out var aa) && aa;
            var disableAud = bool.TryParse(section["DisableAudienceValidation"], out var da) && da;

            if (string.IsNullOrWhiteSpace(authority))
                throw new InvalidOperationException("AppSettings:Authentication:Authority is required.");
            if (!disableAud && string.IsNullOrWhiteSpace(audience))
                throw new InvalidOperationException("AppSettings:Authentication:Audience is required (or set DisableAudienceValidation=true).");

            services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveToken = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),

                    ValidateAudience = !disableAud,
                    ValidAudience = disableAud ? null : audience,

                    // ✅ Delegate (lambda), not a class
                    AudienceValidator = disableAud ? null : (AudienceValidator)((tokenAudiences, securityToken, validationParameters) =>
                    {
                        // 1) Standard 'aud' contains expected audience
                        if (tokenAudiences != null && !string.IsNullOrWhiteSpace(audience) &&
                            tokenAudiences.Any(a => string.Equals(a, audience, StringComparison.OrdinalIgnoreCase)))
                            return true;

                        if (!acceptAzp) return false;

                        // 2) Keycloak 'azp' equals expected audience
                        if (securityToken is JsonWebToken jwt)
                        {
                            if (jwt.TryGetPayloadValue<string>("azp", out var azp) &&
                                !string.IsNullOrWhiteSpace(audience) &&
                                string.Equals(azp, audience, StringComparison.OrdinalIgnoreCase))
                                return true;

                            // 3) Keycloak 'resource_access' contains expected audience as a key
                            if (jwt.TryGetPayloadValue<JsonElement>("resource_access", out var ra) &&
                                ra.ValueKind == JsonValueKind.Object &&
                                !string.IsNullOrWhiteSpace(audience) &&
                                ra.TryGetProperty(audience, out _))
                                return true;
                        }

                        return false;
                    }),

                    ValidateIssuerSigningKey = true
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogError(ctx.Exception, "Authentication failed.");
                        LogTokenSnapshotIfPresent(logger, ctx.HttpContext.Request.Headers.Authorization);
                        return Task.CompletedTask;
                    },

                    OnTokenValidated = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        if (ctx.SecurityToken is JsonWebToken jwt)
                        {
                            logger.LogDebug("✅ Token ok. iss={Iss} sub={Sub} aud=[{Aud}] azp={Azp} scope={Scope}",
                                jwt.Issuer, jwt.Subject,
                                string.Join(",", jwt.Audiences ?? Array.Empty<string>()),
                                jwt.TryGetPayloadValue<string>("azp", out var azp) ? azp : "(none)",
                                jwt.TryGetPayloadValue<string>("scope", out var scope) ? scope : "(none)");
                        }
                        return Task.CompletedTask;
                    },

                    OnChallenge = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        if (ctx.AuthenticateFailure != null)
                            logger.LogError(ctx.AuthenticateFailure, "Authentication challenge failed: {Msg}", ctx.AuthenticateFailure.Message);
                        else if (!string.IsNullOrEmpty(ctx.ErrorDescription))
                            logger.LogWarning("Authentication challenge: {Err} - {Desc}", ctx.Error, ctx.ErrorDescription);
                        else
                            logger.LogWarning("Authentication challenge occurred (no details).");

                        LogTokenSnapshotIfPresent(logger, ctx.HttpContext.Request.Headers.Authorization);
                        return Task.CompletedTask;
                    },

                    OnForbidden = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning("Forbidden: user is authenticated but not authorized. path={Path}",
                            ctx.HttpContext.Request.Path);
                        return Task.CompletedTask;
                    }
                };
            });

            services.AddAuthorization();
            return services;
        }

        private static void LogTokenSnapshotIfPresent(ILogger logger, string authHeader)
        {
            const string bearer = "Bearer ";
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return;

            var token = authHeader.Substring(bearer.Length).Trim();
            if (string.IsNullOrWhiteSpace(token)) return;

            try
            {
                var jwt = new JsonWebToken(token);
                var aud = string.Join(",", jwt.Audiences ?? Array.Empty<string>());
                var hasResourceAccess = jwt.TryGetPayloadValue<JsonElement>("resource_access", out var ra) &&
                                        ra.ValueKind == JsonValueKind.Object
                                      ? string.Join(",", ra.EnumerateObject().Select(p => p.Name))
                                      : "(none)";

                logger.LogWarning("🔎 Token snapshot: iss={Iss} sub={Sub} aud=[{Aud}] azp={Azp} scope={Scope} resource_access=[{RA}]",
                    jwt.Issuer, jwt.Subject,
                    string.IsNullOrEmpty(aud) ? "(none)" : aud,
                    jwt.TryGetPayloadValue<string>("azp", out var azp) ? azp : "(none)",
                    jwt.TryGetPayloadValue<string>("scope", out var scope) ? scope : "(none)",
                    hasResourceAccess);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not parse JWT for diagnostics.");
            }
        }
        public static IServiceCollection AddIDPAuthentication1(this IServiceCollection services, IConfiguration configuration)
        {
            var idpSection = configuration.GetSection("AppSettings:Authentication");
            var idpDomain = idpSection["Authority"];
            var idpAudience = idpSection["Audience"];

            if (string.IsNullOrWhiteSpace(idpDomain))
            {
                throw new InvalidOperationException("IDP:Domain is not configured in appsettings.json.");
            }
            if (string.IsNullOrWhiteSpace(idpAudience))
            {
                throw new InvalidOperationException("IDP:Audience is not configured in appsettings.json.");
            }

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = idpDomain;
                options.Audience = idpAudience;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = idpAudience,
                    // Or support multiple audiences, use:
                    // ValidAudiences = new[] { idpAudience, "another_audience" },
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogError(context.Exception, "Authentication failed.");
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogDebug("Token successfully validated for user: {UserName}", context.Principal?.Identity?.Name);
                        foreach (var claim in context.Principal?.Claims ?? Array.Empty<System.Security.Claims.Claim>())
                        {
                            logger.LogDebug("Claim - Type: {ClaimType}, Value: {ClaimValue}", claim.Type, claim.Value);
                        }
                        return Task.CompletedTask;
                    },
                    OnForbidden = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning("Forbidden: The authenticated user is not authorized to access this resource.");
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerEvents>>();
                        if (context.AuthenticateFailure != null)
                        {
                            // 💡 Log the exact reason for the failure
                            logger.LogError(context.AuthenticateFailure, "Authentication challenge failed: {ErrorMessage}", context.AuthenticateFailure.Message);
                            // Add more detailed tracing here
                            if (context.AuthenticateFailure is SecurityTokenInvalidAudienceException audienceException)
                            {


                                // Log the audience from the token itself
                                //var tokenAudience = audienceException.Token?.Split('.')[1]
                                //    .Replace('-', '+').Replace('_', '/');
                                //var json = Encoding.UTF8.GetString(Convert.FromBase64String(tokenAudience));
                                logger.LogError("Validation failed for audience. Check the token's 'aud' claim and the server's configured 'ValidAudience'.");

                                //logger.LogError("Token Audience: {TokenAudience}", json);
                            }
                        }
                        else if (!string.IsNullOrEmpty(context.ErrorDescription))
                        {
                            logger.LogWarning("Authentication challenge: {Error} - {ErrorDescription}", context.Error, context.ErrorDescription);
                        }
                        else
                        {
                            logger.LogWarning("Authentication challenge occurred (no specific error details provided).");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            services.AddAuthorization();
            return services;
        }

        // This method will be used inside your InstallSwagger extension
        public static void AddSwaggerSecurityDefinition(this SwaggerGenOptions options)
        {
            // Define the OAuth2.0 scheme for Swagger
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer" // lowercase "bearer" is important for Swagger UI
            });

            // Add a security requirement to all operations (or specific ones if preferred)
            //options.AddSecurityRequirement(new OpenApiSecurityRequirement
            //{
            //    {
            //        new OpenApiSecurityScheme
            //        {
            //            Reference = new OpenApiReference
            //            {
            //                Type = ReferenceType.SecurityScheme,
            //                Id = "Bearer"
            //            }
            //        },
            //        Array.Empty<string>() // Add specific scopes if your API uses them beyond basic validation
            //    }
            //});
        }
    }


}
