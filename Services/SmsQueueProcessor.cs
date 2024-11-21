using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class SmsQueueService
    {
        private readonly ISmsProvider _provider;
        private readonly ConcurrentQueue<(SendSmsRequest Request, TaskCompletionSource<Guid> IdTask)> _smsQueue = new();
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

        public Task<Guid> QueueSms(SendSmsRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var tcs = new TaskCompletionSource<Guid>();
            _smsQueue.Enqueue((request, tcs));

            Logger.LogInfo(
                provider: "SmsQueue",
                eventType: "MessageQueued",
                messageID: "",
                details: $"SMS queued for {request.PhoneNumber}");

            return tcs.Task;
        }

        private async void ProcessQueue(object? state)
        {
            if (_smsQueue.TryDequeue(out var item))
            {
                try
                {
                    var (result, messageId) = await _provider.SendSms(item.Request);
                    if (result is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode != 200)
                    {
                        throw new InvalidOperationException($"SMS send failed with status {statusCodeResult.StatusCode}");
                    }

                    item.IdTask.SetResult(messageId);
                    Logger.LogInfo(
                        provider: "SmsQueue",
                        eventType: "MessageSent",
                        messageID: messageId.ToString(),
                        details: $"SMS sent successfully to {item.Request.PhoneNumber}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: "SmsQueue",
                        eventType: "SendFailed",
                        messageID: "",
                        details: $"Failed to send SMS to {item.Request.PhoneNumber}: {ex.Message}");
                    item.IdTask.SetException(ex);
                }
            }
        }
    }
}