using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class PrincipleWebhookService
    {
        private const string SignatureHeader = "X-Principle-Signature";
        private const string TimestampHeader = "X-Principle-Timestamp";

        private readonly Configuration _fileConfiguration;
        private readonly PrincipleApiClient _principleApi;
        private readonly PrincipleOutboundSmsStore _store;
        private readonly SmsQueueService _smsQueue;

        public PrincipleWebhookService(
            Configuration fileConfiguration,
            PrincipleApiClient principleApi,
            PrincipleOutboundSmsStore store,
            SmsQueueService smsQueue)
        {
            _fileConfiguration = fileConfiguration;
            _principleApi = principleApi;
            _store = store;
            _smsQueue = smsQueue;
        }

        public async Task<IResult> HandleAsync(HttpRequest request)
        {
            if (!_principleApi.Enabled)
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: "Principle webhook received but Principle integration is disabled"
                );
                return Results.NotFound();
            }
            else
            {
                // Happy case handled below
            }

            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "PrincipleWebhook",
                details: $"Principle webhook received from {request.HttpContext.Connection.RemoteIpAddress}"
            );

            var body = await ReadBodyAsync(request);

            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "PrincipleWebhookPayload",
                details: $"Raw webhook body: {body}"
            );

            var secret = _fileConfiguration.GetSetting("Principle:WEBHOOK_SECRET");
            if (string.IsNullOrWhiteSpace(secret))
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "Configuration",
                    details: "Principle webhook secret is not configured but Principle is enabled"
                );
                return Results.Problem(
                    title: "SMS Gateway Configuration Error",
                    detail: "Principle webhook secret is not configured",
                    statusCode: 500
                );
            }
            else
            {
                // Happy case handled below
            }

            if (!request.Headers.TryGetValue(SignatureHeader, out var signature) ||
                !request.Headers.TryGetValue(TimestampHeader, out var timestamp))
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook rejected: missing X-Principle-Signature or X-Principle-Timestamp header from {request.HttpContext.Connection.RemoteIpAddress}"
                );
                return Results.Unauthorized();
            }
            else
            {
                // Happy case handled below
            }

            if (!VerifySignature(secret, timestamp.ToString(), body, signature.ToString()))
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook rejected: invalid signature from {request.HttpContext.Connection.RemoteIpAddress}"
                );
                return Results.BadRequest("Invalid Principle webhook signature");
            }
            else
            {
                // Happy case handled below
            }

            PrincipleWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize(body, AppJsonSerializerContext.Default.PrincipleWebhookPayload);
            }
            catch (JsonException)
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook rejected: invalid JSON body from {request.HttpContext.Connection.RemoteIpAddress}"
                );
                return Results.BadRequest("Invalid Principle webhook JSON");
            }

            if (payload?.Data == null)
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook rejected: null payload data from {request.HttpContext.Connection.RemoteIpAddress}"
                );
                return Results.BadRequest("Principle webhook payload is empty");
            }
            else
            {
                // Happy case handled below
            }

            var data = payload.Data;
            if (string.IsNullOrWhiteSpace(data.Id) ||
                string.IsNullOrWhiteSpace(data.PatientPhoneNumber) ||
                string.IsNullOrWhiteSpace(data.Body) ||
                string.IsNullOrWhiteSpace(data.PracticeId))
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook rejected: missing required fields (id={data.Id}, phone={data.PatientPhoneNumber}, body={(data.Body != null ? "present" : "missing")}, practiceId={data.PracticeId})"
                );
                return Results.BadRequest("Principle SMS webhook is missing required message fields");
            }
            else
            {
                // Happy case handled below
            }

            if (string.Equals(data.Direction, "outbound", StringComparison.OrdinalIgnoreCase))
            {
                if (await _store.ExistsAsync(data.Id))
                {
                    Logger.LogInfo(
                        provider: SmsProviderType.JustRemotePhone,
                        eventType: "PrincipleWebhook",
                        details: $"Principle webhook duplicate: message id {data.Id} already processed"
                    );
                    return Results.Ok(new { status = "duplicate" });
                }
                else
                {
                    // Happy case handled below
                }

                var smsBridgeId = _smsQueue.QueueSms(new SendSmsRequest(
                    PhoneNumber: data.PatientPhoneNumber,
                    Message: data.Body,
                    CallbackUrl: null,
                    SenderId: data.PracticePhoneNumber
                ));

                await _store.AddAsync(new PrincipleOutboundSmsMapRecord(
                    PrincipleMessageId: data.Id,
                    SmsBridgeId: smsBridgeId.Value.ToString(),
                    PatientPhoneNumber: data.PatientPhoneNumber,
                    PatientId: data.PatientId,
                    PracticeId: data.PracticeId,
                    Body: data.Body,
                    ReceivedAt: DateTime.Now
                ));

                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleOutboundSmsQueued",
                    SMSBridgeID: smsBridgeId,
                    details: $"Queued Principle outbound SMS {data.Id} to {data.PatientPhoneNumber}"
                );

                return Results.Ok(new { status = "queued", smsBridgeId = smsBridgeId.Value.ToString() });
            }
            else
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "PrincipleWebhook",
                    details: $"Principle webhook skipped: direction is '{data.Direction}' (only 'outbound' is processed)"
                );
                return Results.Ok(new { status = "skipped", reason = "not outbound" });
            }
        }

        private static bool VerifySignature(string secret, string timestamp, string body, string signature)
        {
            var signedBody = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedBody))
                .ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant())
            );
        }

        private static async Task<string> ReadBodyAsync(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }
}
