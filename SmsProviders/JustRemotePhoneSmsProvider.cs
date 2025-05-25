﻿using System;
using SMS_Bridge.Models;
using JustRemotePhone.RemotePhoneService;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Collections.Generic;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Linq;
using System.Threading.Tasks;

namespace SMS_Bridge.SmsProviders
{


    public class JustRemotePhoneSmsProvider : ISmsProvider
    {
        // Event that fires when a message times out waiting for delivery confirmation
        public event Action<SmsBridgeId, string[]> OnMessageTimeout = delegate { };

        private static Application _app = new Application("SMS Bridge");
        private static bool _isConnected = false;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private static readonly int _connectionTimeoutMs = 10000; // 10 seconds
        private static readonly ConcurrentDictionary<SmsBridgeId, Timer> _messageTimers = new();
        private const int MESSAGE_TIMEOUT_MS = 630000; // 10.5 minutes
        private static readonly ConcurrentDictionary<SmsBridgeId, (ProviderMessageId ProviderMessageID, SmsStatus Status, DateTime SentAt, DateTime StatusAt)> _messageStatuses = new();
        // NOTE: This means we are looking up messages by SMSBridgeID, which is guaranteed different to ProviderMessageID
        // be careful you use it correctly
        
        private readonly Timer _connectionHealthCheckTimer;
        private const int CONNECTION_CHECK_INTERVAL_MS = 3600000; // Check connection every hour

        private readonly SmsReceivedHandler _smsReceivedHandler;


        private void RefreshConnection(object? state)
        {
            try
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "ConnectionHealthCheck",
                    details: $"Performing periodic connection refresh, current state: {_isConnected}"
                );

                // Always reconnect even if we think we're already connected to ensure reliability
                _app.BeginConnect(true);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "ConnectionRefreshFailed",
                    details: $"Failed to refresh connection: {ex.Message}"
                );
            }
        }


        public JustRemotePhoneSmsProvider()
        {
            _smsReceivedHandler = new SmsReceivedHandler(SmsProviderType.JustRemotePhone);

            _app.ApplicationStateChanged += OnApplicationStateChanged;
            _app.Phone.SMSSendResult += OnSMSSendResult;
            _app.Phone.SMSReceived += OnSMSReceived;

            OnMessageTimeout += HandleMessageTimeout;

            _app.BeginConnect(true);
            _connectionHealthCheckTimer = new Timer(RefreshConnection, null,
                CONNECTION_CHECK_INTERVAL_MS, CONNECTION_CHECK_INTERVAL_MS);
        }




        private void OnApplicationStateChanged(ApplicationState newState, ApplicationState oldState)
        {
            _isConnected = newState == ApplicationState.Connected;
            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "ApplicationStateChanged",
                details: $"NewState: {newState}, OldState: {oldState}"
            );

        }

        private async Task<bool> EnsureConnectedAsync()
        {
            if (_isConnected) return true;

            _app.BeginConnect(true);

            // Retry loop for connection
            for (int i = 0; i < 10; i++) // 10 attempts, 2 seconds each
            {
                if (_isConnected) return true;
                await Task.Delay(2000);
            }

            Logger.LogError(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "ConnectionTimeout",
                details: $"Connection attempt timed out after {_connectionTimeoutMs}ms"
            );
            return false;
        }

        private void SetupMessageTimeout(SmsBridgeId smsBridgeId, string[] numbers)
        {
            var timer = new Timer(_ =>
            {
                if (_messageTimers.TryRemove(smsBridgeId, out var timerToDispose))
                {
                    timerToDispose.Dispose();
                    OnMessageTimeout?.Invoke(smsBridgeId, numbers);
                }
            }, null, MESSAGE_TIMEOUT_MS, Timeout.Infinite);

            _messageTimers[smsBridgeId] = timer;
        }


        private void HandleMessageTimeout(SmsBridgeId smsBridgeId, string[] numbers)
        {
            // messageId here is actually SMSBridgeID based on SetupMessageTimeout
            if (_messageStatuses.TryGetValue(smsBridgeId, out var existing) && existing.StatusAt == DateTime.MinValue)
            {
                var timeoutInterval = DateTime.Now - existing.SentAt;
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "Timeout",
                    SMSBridgeID: smsBridgeId, 
                    providerMessageID: existing.ProviderMessageID, 
                    details: $"Message timed out after {timeoutInterval.TotalMinutes:F1} minutes. Numbers: {string.Join(",", numbers)}"
                );
                // Preserve original metadata while updating status
                _messageStatuses[smsBridgeId] = (existing.ProviderMessageID, SmsStatus.TimedOut, existing.SentAt, DateTime.Now);
            }
        }


        private void OnSMSSendResult(Guid providerMessageIdGuid, string[] numbers, SMSSentResult[] results)
        {
            // Convert raw Guid to our typed ProviderMessageId for consistency across the codebase
            var providerMessageId = new ProviderMessageId(providerMessageIdGuid);
            
            // Look up which SmsBridgeId corresponds to this provider message
            var entry = _messageStatuses.FirstOrDefault(e => e.Value.ProviderMessageID.Value == providerMessageIdGuid);

            if (entry.Key != default) // Check if an entry was found (default for SmsBridgeId is Guid.Empty)
            {
                var smsBridgeId = entry.Key;
                var existing = entry.Value;
                var now = DateTime.Now;
                var status = results.FirstOrDefault() switch
                {
                    SMSSentResult.Ok => SmsStatus.Delivered,
                    SMSSentResult.ErrorGeneric => SmsStatus.Failed,
                    _ => SmsStatus.Unknown
                };

                // Preserve original metadata while updating status
                _messageStatuses[smsBridgeId] = (existing.ProviderMessageID, status, existing.SentAt, now);

                var deliveryInterval = now - existing.SentAt;
                for (int i = 0; i < numbers.Length; i++)
                {
                    Logger.LogInfo(
                        provider: SmsProviderType.JustRemotePhone,
                        eventType: "DeliveryStatus",
                        SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                        providerMessageID: providerMessageId, // Pass ProviderMessageId
                        details: $"Number: {numbers[i]}, Status: {status}, Delivery Time: {deliveryInterval.TotalSeconds:F1} seconds"
                    );
                }

                // Clean up the timeout timer since we received a status
                if (_messageTimers.TryRemove(smsBridgeId, out var timer))
                {
                    timer.Dispose();
                }
            }
            else
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "UnexpectedDeliveryStatus",
                    SMSBridgeID: default, // SMSBridgeID is unknown in this case, pass default
                    providerMessageID: providerMessageId, // Pass ProviderMessageId
                    details: $"Received delivery status but no send time recorded. Numbers: {string.Join(",", numbers)}"
                );
                // We cannot add to _messageStatuses here as we don't have the SMSBridgeID
            }
        }
        private void OnSMSReceived(string number, string contactLabel, string text)
        {
            // For JustRemotePhone, we don't have a provider message ID in the callback!!
            // Validated from the API documentation 20250525
            // https://www.justremotephone.com/sdk/Help/index.php
            // Generate a new one to maintain consistency with the updated interface
            string providerMessageId = Guid.NewGuid().ToString();
            
            _smsReceivedHandler.HandleSmsReceived(
                number: number,
                contactLabel: contactLabel,
                text: text,
                providerMessageIdString: providerMessageId
            );
        }


        public async Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            // Validation check
            if (request.PhoneNumber.Contains(',') || request.PhoneNumber.Contains(';'))
            {
                var result = Results.Problem(
                    detail: "This SMS bridge only supports sending to single numbers",
                    statusCode: 400,
                    title: "Invalid Request"
                );
                return (Result: result, smsBridgeId: default); // Use default for SmsBridgeId
            }

            // Connection check.
            // I've added the log because I think this is redundant and I'm going to monitor the log to see if it ever happens
            if (!_isConnected)
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendSMS",
                    SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                    providerMessageID: default, // ProviderMessageID is not available yet
                    details: $"Starting to send an SMS but RemotePhone is not connected.  Attempting to Connect "
                );

                if (!await EnsureConnectedAsync())
                {
                    var result = Results.Problem(
                        detail: "Failed to connect to SMS service after timeout",
                        statusCode: 503,
                        title: "SMS Service Unavailable"
                    );
                    return (Result: result, smsBridgeId: smsBridgeId); // Match parameter name in interface
                }
            }


            try
            {
                string[] numbers = { request.PhoneNumber };
                string text = request.Message;
                Guid providerMessageId; // This will be populated by SendSMS

                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendAttempt",
                    SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                    providerMessageID: default, // ProviderMessageID is not available yet
                    details: $"PhoneNumber: {request.PhoneNumber}, Message: {request.Message}"
                );

                _app.Phone.SendSMS(numbers, text, out providerMessageId);
                // Store the mapping between our internal ID and the provider's ID for future reference
                _messageStatuses[smsBridgeId] = (new ProviderMessageId(providerMessageId), SmsStatus.Pending, DateTime.Now, DateTime.MinValue);
                SetupMessageTimeout(smsBridgeId, numbers);

                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendSuccess",
                    SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                    providerMessageID: new ProviderMessageId(providerMessageId), // Pass ProviderMessageId
                    details: $"PhoneNumber: {request.PhoneNumber}"
                );

                var result = Results.Ok(new Result(
                    Success: true,
                    Message: "SMS queued for sending"
                ));

                return (Result: result, smsBridgeId: smsBridgeId); // Return smsBridgeId
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendFailure",
                    SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                    providerMessageID: default, // ProviderMessageID is not available on failure
                    details: $"PhoneNumber: {request.PhoneNumber}, Error: {ex.Message}"
                );

                var errorResult = Results.Problem(
                    detail: "Failed to send SMS: " + ex.Message,
                    statusCode: 500,
                    title: "SMS Send Failure"
                );

                return (Result: errorResult, smsBridgeId: default); // Use default for SmsBridgeId
            }
        }

        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            return _smsReceivedHandler.GetReceivedMessages();
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            var now = DateTime.Now;
            var recentStatuses = _messageStatuses
                .Where(entry => (now - entry.Value.SentAt).TotalHours <= 24) // Example: Last 24 hours
                .Select(entry => new MessageStatusRecord(
                    SMSBridgeID: entry.Key,
                    ProviderMessageID: entry.Value.ProviderMessageID,   // do we have a provider MessageID?
                    Status: entry.Value.Status,
                    SentAt: entry.Value.SentAt,
                    StatusAt: entry.Value.StatusAt
                ))
                .ToList();

            return Task.FromResult((IEnumerable<MessageStatusRecord>)recentStatuses);

        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId)
        {
            return _smsReceivedHandler.DeleteReceivedMessage(smsBridgeId);
        }





        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId)
        {
            if (_messageStatuses.TryGetValue(smsBridgeId, out var messageData))
            {
                return messageData.ProviderMessageID;
            }
            
            Logger.LogWarning(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "UnknownProviderMessageID",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default,
                details: "Provider message ID lookup for unknown SMS bridge ID"
            );
            
            return null;
        }

        public Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId)
        {
            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "StatusCheck",
                SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                providerMessageID: default, // ProviderMessageID is not directly available here
                details: $"Timer exists: {_messageTimers.ContainsKey(smsBridgeId)}, Status exists: {_messageStatuses.ContainsKey(smsBridgeId)}"
            );

            if (_messageStatuses.TryGetValue(smsBridgeId, out var messageData))
            {
                return Task.FromResult(messageData.Status switch
                {
                    SmsStatus.Delivered => SmsStatus.Delivered,
                    SmsStatus.Pending => SmsStatus.Pending,
                    _ => SmsStatus.Failed
                });
            }

            Logger.LogWarning(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "UnknownMessageStatus",
                SMSBridgeID: smsBridgeId, // Pass SmsBridgeId
                providerMessageID: default, // ProviderMessageID is not directly available here
                details: "Status check for unknown message ID"
            );

            return Task.FromResult(SmsStatus.Failed);
        }
    }
}
