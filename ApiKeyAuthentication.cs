using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge
{
    /// <summary>
    /// Every request needs a valid X-API-Key unless its endpoint opts out with AllowAnonymous().
    /// The fallback policy also covers endpoints mapped outside any group and unmatched routes,
    /// and authorization runs before the endpoint binds its request body.
    /// </summary>
    public static class ApiKeyAuthentication
    {
        public const string Scheme = "ApiKey";

        // No Host- or IP-based exemption: Host is client-controlled, and the reverse proxy
        // makes every request arrive from the proxy's address.
        public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services, string apiKey, SmsProviderType provider)
        {
            services.AddAuthentication(Scheme)
                .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(Scheme, options =>
                {
                    options.ApiKey = apiKey;
                    options.Provider = provider;
                });

            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder(Scheme)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            return services;
        }
    }

    public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
    {
        public string ApiKey { get; set; } = "";
        public SmsProviderType Provider { get; set; }
    }

    public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var expected = Encoding.UTF8.GetBytes(Options.ApiKey);

            if (expected.Length == 0
                || !Request.Headers.TryGetValue("X-API-Key", out var sent)
                || sent.Count != 1
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent[0] ?? ""), expected))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            else
            {
                // Happy case handled below
            }

            var identity = new ClaimsIdentity(ApiKeyAuthentication.Scheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyAuthentication.Scheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // AuthenticationHandler has its own Logger property, hence the qualified name below.
        // Runs only when an endpoint that needs the key is requested without a valid one.
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Services.Logger.LogWarning(
                provider: Options.Provider,
                eventType: "UnauthorizedAccess",
                details: $"Missing/invalid global API key from {Context.Connection.RemoteIpAddress} for {Request.Method} {Request.Path}",
                SMSBridgeID: default
            );
            return base.HandleChallengeAsync(properties);
        }
    }
}
