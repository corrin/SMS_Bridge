﻿using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class SmsQueueService
    {
        private readonly ISmsProvider _provider;
        private readonly SmsProviderType _providerType;
        private readonly ConcurrentQueue<(SendSmsRequest Request, SmsBridgeId smsBridgeId)> _smsQueue = new();
        private readonly ConcurrentDictionary<SmsBridgeId, ProviderMessageId> _smsbridgetoproviderid = new();
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
                SMSBridgeID: default,
                providerMessageID: default,
                details: "SMS Queue initialized");
        }

        public SmsBridgeId QueueSms(SendSmsRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var smsBridgeId = new SmsBridgeId(Guid.NewGuid());
            _smsQueue.Enqueue((request, smsBridgeId));

            Logger.LogInfo(
                provider: _providerType,
                eventType: "MessageQueued",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default, // providerMessageID is not available when queuing
                details: $"SMS queued for {request.PhoneNumber}");

            return smsBridgeId;
        }

        private async void ProcessQueue(object? state)
        {
            if (_smsQueue.TryDequeue(out var item))
            {
                var (request, smsBridgeId) = item;
                try
                {
                    var (result, returnedSmsBridgeId) = await _provider.SendSms(request, smsBridgeId);
                    if (result is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode != 200)
                    {
                        throw new InvalidOperationException($"SMS send failed with status {statusCodeResult.StatusCode}");
                    }

                    // Retrieve the mapping to ensure we can track message status
                    var providerMessageId = _provider.GetProviderMessageID(smsBridgeId);
                    if (providerMessageId != null)
                    {
                        _smsbridgetoproviderid[smsBridgeId] = providerMessageId.Value;
                    }

                    Logger.LogInfo(
                        provider: _providerType,
                        eventType: "MessageSent",
                        SMSBridgeID: smsBridgeId,
                        providerMessageID: providerMessageId ?? default,
                        details: $"Mapped to providerMessageID (SMSBridgeID): {(providerMessageId?.ToString() ?? "unknown")} ({smsBridgeId}), SMS sent to {request.PhoneNumber}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: _providerType,
                        eventType: "SendFailed",
                        SMSBridgeID: smsBridgeId,
                        providerMessageID: default, // providerMessageID might not be available on failure
                        details: $"Failed to send SMS to {request.PhoneNumber}: {ex.Message}");
                }
            }
        }

        public bool TryGetProviderMessageID(Guid smsBridgeIdGuid, out Guid providerMessageIdGuid)
        {
            var smsBridgeId = new SmsBridgeId(smsBridgeIdGuid);
            providerMessageIdGuid = Guid.Empty;
            
            if (_smsbridgetoproviderid.TryGetValue(smsBridgeId, out var providerMessageId))
            {
                providerMessageIdGuid = providerMessageId.Value;
                return true;
            }
            
            return false;
        }
    }
}
