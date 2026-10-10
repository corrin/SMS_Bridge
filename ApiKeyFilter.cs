using System.Security.Cryptography;
using System.Text;
using SMS_Bridge.SmsProviders;
using SMS_Bridge.Services;

namespace SMS_Bridge
{
    /// <summary>
    /// Maps /smsgateway as a route group whose every endpoint, including nested groups, requires X-API-Key.
    /// Webhooks authenticate per-provider and must be mapped in a separate group, not under this one.
    /// </summary>
    public static class ApiKeyFilter
    {
        // No Host- or IP-based exemption: Host is client-controlled, and the reverse proxy
        // makes every request arrive from the proxy's address.
        public static RouteGroupBuilder MapKeyedGroup(IEndpointRouteBuilder app, string apiKey, SmsProviderType provider)
        {
            var expected = Encoding.UTF8.GetBytes(apiKey);

            return app.MapGroup("/smsgateway")
                .AddEndpointFilter(async (context, next) =>
                {
                    var http = context.HttpContext;

                    if (!http.Request.Headers.TryGetValue("X-API-Key", out var sent)
                        || sent.Count != 1
                        || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent[0] ?? ""), expected))
                    {
                        Logger.LogWarning(
                            provider: provider,
                            eventType: "UnauthorizedAccess",
                            details: $"Missing/invalid global API key from {http.Connection.RemoteIpAddress}, Host {http.Request.Host}",
                            SMSBridgeID: default
                        );
                        return Results.Unauthorized();
                    }
                    else
                    {
                        // Happy case handled below
                    }

                    return await next(context);
                });
        }
    }
}
