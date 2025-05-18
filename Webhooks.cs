using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using System.Text.Json;

namespace SMS_Bridge
{
    /// <summary>
    /// Static helper to configure webhook endpoints for registered SMS providers.
    /// The application configuration must include each provider's callback key under SmsSettings:Providers:{provider}:CallbackKey.
    /// </summary>
    public static class Webhooks
    {
        /// <summary>
        /// Registers POST endpoints for provider-specific webhook events.
        /// URL pattern: /smsgateway/webhooks/{provider}/{event}
        /// Example: POST /smsgateway/webhooks/etxt/inbound
        /// </summary>
        /// <param name="webhooksApi">The RouteGroupBuilder for /smsgateway/webhooks</param>
        /// <param name="configuration">Application configuration for reading callback keys</param>
        public static void RegisterWebhookEndpoints(
            RouteGroupBuilder webhooksApi,
            IConfiguration configuration)
        {
            webhooksApi.MapPost("/{provider}/{event}", async (
                string provider,
                string @event,
                HttpRequest httpRequest,
                SmsQueueService smsQueue,
                IConfiguration config) =>
            {
                // Build config path for this provider's callback key
                var keyPath = $"SmsSettings:Providers:{provider}:CallbackKey";
                var expectedKey = config[keyPath];
                if (string.IsNullOrWhiteSpace(expectedKey))
                {
                    return Results.BadRequest($"Unknown provider '{provider}'");
                }

                // Validate the provider-specific header
                var headerName = $"X-{provider}-Callback-Key";
                if (!httpRequest.Headers.TryGetValue(headerName, out var providedKey)
                    || providedKey != expectedKey)
                {
                    return Results.Unauthorized();
                }

                // Deserialize payload into shared model
                ReceiveSmsRequest payload;
                try
                {
                    payload = await JsonSerializer.DeserializeAsync<ReceiveSmsRequest>(
                        httpRequest.Body,
                        AppJsonSerializerContext.Default.ReceiveSmsRequest)
                        ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    return Results.BadRequest("Invalid JSON payload for webhook");
                }

                // Actually handle it
                // smsQueue.QueueIncomingSms(provider, payload);

                // Acknowledge receipt
                return Results.Ok(new { status = "received", provider, @event });
            });
        }
    }
}
