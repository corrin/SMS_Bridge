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
                return Results.NotFound();
            }

            var body = await ReadBodyAsync(request);
            var secret = _fileConfiguration.GetSetting("Principle:WEBHOOK_SECRET") ?? "";
            if (!string.IsNullOrWhiteSpace(secret))
            {
                if (!request.Headers.TryGetValue(SignatureHeader, out var signature) ||
                    !request.Headers.TryGetValue(TimestampHeader, out var timestamp))
                {
                    return Results.Unauthorized();
                }

                if (!VerifySignature(secret, timestamp.ToString(), body, signature.ToString()))
                {
                    return Results.BadRequest("Invalid Principle webhook signature");
                }
            }

            PrincipleWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize(body, AppJsonSerializerContext.Default.PrincipleWebhookPayload);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid Principle webhook JSON");
            }

            if (payload?.Data == null || !IsSendableOutboundMessage(payload))
            {
                return Results.Ok(new { status = "ignored" });
            }

            var data = payload.Data;
            if (string.IsNullOrWhiteSpace(data.Id) ||
                string.IsNullOrWhiteSpace(data.PatientPhoneNumber) ||
                string.IsNullOrWhiteSpace(data.Body) ||
                string.IsNullOrWhiteSpace(data.PracticeId))
            {
                return Results.BadRequest("Principle SMS webhook is missing required message fields");
            }

            if (await _store.ExistsAsync(data.Id))
            {
                return Results.Ok(new { status = "duplicate" });
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

        private static bool IsSendableOutboundMessage(PrincipleWebhookPayload payload)
        {
            return payload.EventType == "message.created" &&
                payload.Data?.Direction == "outbound" &&
                payload.Data?.Status == "pending";
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
