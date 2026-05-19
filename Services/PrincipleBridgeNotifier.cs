using System.Text;
using System.Text.Json;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class PrincipleBridgeOptions
    {
        public bool Enabled { get; set; }
        public string InboundSmsUrl { get; set; } = "http://127.0.0.1:8787/webhooks/provider/inbound-sms";
        public string ApiKey { get; set; } = "";
        public int TimeoutMs { get; set; } = 10000;
        public bool DeleteAfterSuccessfulCallback { get; set; }
    }

    public class PrincipleBridgeNotifier
    {
        private readonly HttpClient _httpClient;
        private readonly PrincipleBridgeOptions _options;

        public PrincipleBridgeNotifier(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _options = configuration.GetSection("PrincipleBridge").Get<PrincipleBridgeOptions>()
                ?? new PrincipleBridgeOptions();
        }

        public bool DeleteAfterSuccessfulCallback => _options.DeleteAfterSuccessfulCallback;

        public async Task<bool> NotifyInboundSmsAsync(
            SmsProviderType provider,
            ReceiveSmsRequest sms,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.InboundSmsUrl))
            {
                Logger.LogWarning(
                    provider: provider,
                    eventType: "PrincipleBridgeCallbackSkipped",
                    details: "PrincipleBridge is enabled but InboundSmsUrl is not configured",
                    SMSBridgeID: sms.MessageID,
                    providerMessageID: sms.ProviderMessageID
                );
                return false;
            }

            var payload = new PrincipleBridgeInboundSmsNotification(
                From: sms.FromNumber,
                Body: sms.MessageText,
                ProviderMessageId: sms.ProviderMessageID.Value.ToString(),
                SmsBridgeId: sms.MessageID.Value.ToString(),
                ReceivedAt: sms.ReceivedAt
            );

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _options.TimeoutMs)));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.InboundSmsUrl)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            payload,
                            AppJsonSerializerContext.Default.PrincipleBridgeInboundSmsNotification),
                        Encoding.UTF8,
                        "application/json"
                    )
                };

                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    request.Headers.Add("X-API-Key", _options.ApiKey);
                }

                using var response = await _httpClient.SendAsync(request, timeout.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInfo(
                        provider: provider,
                        eventType: "PrincipleBridgeCallbackSucceeded",
                        details: $"Principle bridge accepted inbound SMS. Status: {(int)response.StatusCode}",
                        SMSBridgeID: sms.MessageID,
                        providerMessageID: sms.ProviderMessageID
                    );
                    return true;
                }

                Logger.LogWarning(
                    provider: provider,
                    eventType: "PrincipleBridgeCallbackFailed",
                    details: $"Principle bridge returned HTTP {(int)response.StatusCode}: {responseBody}",
                    SMSBridgeID: sms.MessageID,
                    providerMessageID: sms.ProviderMessageID
                );
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    provider: provider,
                    eventType: "PrincipleBridgeCallbackFailed",
                    details: $"Principle bridge callback failed: {ex.Message}",
                    SMSBridgeID: sms.MessageID,
                    providerMessageID: sms.ProviderMessageID
                );
                return false;
            }
        }
    }
}
