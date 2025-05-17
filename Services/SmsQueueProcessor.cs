using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class SmsQueueService
    {
        private readonly ISmsProvider _provider;
        private readonly ConcurrentQueue<(SendSmsRequest Request, Guid ExternalId)> _smsQueue = new();
        private readonly ConcurrentDictionary<Guid, Guid> _externalToInternal = new();
        private readonly Timer _processTimer;
        private const int PROCESS_INTERVAL_MS = 5000;

        public SmsQueueService(ISmsProvider provider, IConfiguration configuration)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _processTimer = new Timer(ProcessQueue, null, PROCESS_INTERVAL_MS, PROCESS_INTERVAL_MS);

            Logger.LogInfo(
                provider: "SmsQueue",
                eventType: "Initialization",
                messageID: "",
                details: "SMS Queue initialized");
        }

        public Guid QueueSms(SendSmsRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var externalId = Guid.NewGuid();
            _smsQueue.Enqueue((request, externalId));

            Logger.LogInfo(
                provider: "SmsQueue",
                eventType: "MessageQueued",
                messageID: externalId.ToString(),
                details: $"SMS queued for {request.PhoneNumber}");

            return externalId;
        }

        private async void ProcessQueue(object? state)
        {
            if (_smsQueue.TryDequeue(out var item))
            {
                var (request, externalId) = item;
                try
                {
                    var (result, internalId) = await _provider.SendSms(request);
                    if (result is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode != 200)
                    {
                        throw new InvalidOperationException($"SMS send failed with status {statusCodeResult.StatusCode}");
                    }

                    _externalToInternal[externalId] = internalId;

                    Logger.LogInfo(
                        provider: "SmsQueue",
                        eventType: "MessageSent",
                        messageID: externalId.ToString(),
                        details: $"Mapped to internalId (SMSBridgeID): {internalId}, SMS sent to {request.PhoneNumber}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: "SmsQueue",
                        eventType: "SendFailed",
                        messageID: externalId.ToString(),
                        details: $"Failed to send SMS to {request.PhoneNumber}: {ex.Message}");
                }
            }
        }

        public bool TryGetInternalId(Guid externalId, out Guid internalId)
        {
            return _externalToInternal.TryGetValue(externalId, out internalId);
        }
    }
}
