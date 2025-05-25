﻿using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class SmsQueueService
    {
        private readonly ISmsProvider _provider;
        private readonly SmsProviderType _providerType;
        private readonly ConcurrentQueue<(SendSmsRequest Request, Guid SMSBridgeID)> _smsQueue = new();
        private readonly ConcurrentDictionary<Guid, Guid> _smsbridgetoproviderid = new();
        private readonly Timer _processTimer;
        private const int PROCESS_INTERVAL_MS = 5000;

        public SmsQueueService(ISmsProvider provider, IConfiguration configuration)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _processTimer = new Timer(ProcessQueue, null, PROCESS_INTERVAL_MS, PROCESS_INTERVAL_MS);

            _providerType = provider switch
            {
                JustRemotePhoneSmsProvider => SmsProviderType.JustRemotePhone,
                ETxtSmsProvider => SmsProviderType.ETxt,
                DiafaanSmsProvider => SmsProviderType.Diafaan,
                _ => SmsProviderType.BuggyCodeNeedsFixing // Fallback for unsupported providers
            };

            Logger.LogInfo(
                provider: _providerType,
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

            var SMSBridgeID = Guid.NewGuid();
            _smsQueue.Enqueue((request, SMSBridgeID));

            Logger.LogInfo(
                provider: _providerType,
                eventType: "MessageQueued",
                messageID: SMSBridgeIDId.ToString(),
                details: $"SMS queued for {request.PhoneNumber}");

            return SMSBridgeID;
        }

        private async void ProcessQueue(object? state)
        {
            if (_smsQueue.TryDequeue(out var item))
            {
                var (request, SMSBridgeID) = item;
                try
                {
                    var (result, providerMessageID) = await _provider.SendSms(request);
                    if (result is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode != 200)
                    {
                        throw new InvalidOperationException($"SMS send failed with status {statusCodeResult.StatusCode}");
                    }

                    _smsbridgetoproviderid[SMSBridgeID] = providerMessageID;

                    Logger.LogInfo(
                        provider: _providerType,
                        eventType: "MessageSent",
                        messageID: SMSBridgeID.ToString(),
                        details: $"Mapped to providerMessageID (SMSBridgeID): {providerMessageID} ({SMSBridgeID.ToString()}), SMS sent to {request.PhoneNumber}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: _providerType,
                        eventType: "SendFailed",
                        messageID: SMSBridgeID.ToString(),
                        details: $"Failed to send SMS to {request.PhoneNumber}: {ex.Message}");
                }
            }
        }

        public bool TryGetProviderMessageID(Guid SMSBridgeID, out Guid providerMessageID)
        {
            return _smsbridgetoproviderid.TryGetValue(SMSBridgeID, out providerMessageID);
        }
    }
}
