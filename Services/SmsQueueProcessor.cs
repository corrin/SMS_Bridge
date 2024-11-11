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
        private readonly int _processInterval = 5000; // 5 seconds
        private bool _isProcessing;

        public SmsQueueService(ISmsProvider provider)
        {
            _provider = provider;
            _processTimer = new Timer(ProcessQueue, null, _processInterval, _processInterval);
        }

        public Task<Guid> QueueSms(SendSmsRequest request)
        {
            var tcs = new TaskCompletionSource<Guid>();
            _smsQueue.Enqueue((request, tcs));
            return tcs.Task;
        }

        private async void ProcessQueue(object? state)
        {
            if (_isProcessing) return;

            _isProcessing = true;
            try
            {
                while (_smsQueue.TryDequeue(out var item))
                {
                    try
                    {
                        var (result, messageId) = await _provider.SendSms(item.Request);
                        item.IdTask.SetResult(messageId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(
                            provider: "SmsQueue",
                            eventType: "SendFailed",
                            messageID: "",
                            details: ex.Message);
                        item.IdTask.SetException(ex);
                    }
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }

    }
}