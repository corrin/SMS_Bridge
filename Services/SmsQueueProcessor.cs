using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class SmsQueueService : IDisposable
    {
        private readonly ISmsProvider _provider;
        private readonly ConcurrentQueue<(SendSmsRequest Request, TaskCompletionSource<Guid> IdTask)> _smsQueue = new();
        private readonly Timer _processTimer;
        private readonly int _processInterval;
        private readonly int _batchSize;
        private volatile bool _isProcessing;
        private volatile bool _disposed;

        public SmsQueueService(ISmsProvider provider, IConfiguration configuration)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _processInterval = configuration.GetValue<int>("QueueSettings:ProcessInterval", 5000);
            _batchSize = configuration.GetValue<int>("QueueSettings:BatchSize", 10);

            if (_processInterval < 1000) // Ensure minimum interval of 1 second
            {
                Logger.LogWarning(
                    provider: "SmsQueue",
                    eventType: "Configuration",
                    messageID: "",
                    details: $"Process interval {_processInterval}ms is too low, using 1000ms instead");
                _processInterval = 1000;
            }

            _processTimer = new Timer(ProcessQueue, null, _processInterval, _processInterval);

            Logger.LogInfo(
                provider: "SmsQueue",
                eventType: "Initialization",
                messageID: "",
                details: $"SMS Queue initialized with interval {_processInterval}ms and batch size {_batchSize}");
        }

        public Task<Guid> QueueSms(SendSmsRequest request)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SmsQueueService));
            }

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
            if (_isProcessing || _disposed) return;

            _isProcessing = true;
            try
            {
                var batch = new List<(SendSmsRequest Request, TaskCompletionSource<Guid> IdTask)>();
                while (batch.Count < _batchSize && _smsQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    Logger.LogInfo(
                        provider: "SmsQueue",
                        eventType: "BatchProcessing",
                        messageID: "",
                        details: $"Processing batch of {batch.Count} messages");
                }

                foreach (var item in batch)
                {
                    if (_disposed) // Check for disposal during processing
                    {
                        item.IdTask.SetCanceled();
                        continue;
                    }

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
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: "SmsQueue",
                    eventType: "BatchProcessingFailed",
                    messageID: "",
                    details: $"Batch processing failed: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            try
            {
                _processTimer?.Dispose();

                // Complete any remaining tasks as canceled
                while (_smsQueue.TryDequeue(out var item))
                {
                    item.IdTask.SetCanceled();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: "SmsQueue",
                    eventType: "DisposalError",
                    messageID: "",
                    details: $"Error during disposal: {ex.Message}");
            }

            GC.SuppressFinalize(this);
        }
    }
}