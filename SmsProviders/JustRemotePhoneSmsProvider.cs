﻿using System;
using SMS_Bridge.Models;
using JustRemotePhone.RemotePhoneService;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Collections.Generic;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Linq; // Added for LINQ methods
using System.Threading.Tasks; // Added for Task

namespace SMS_Bridge.SmsProviders
{


    public class JustRemotePhoneSmsProvider : ISmsProvider
    {
        public event Action<Guid, string[]> OnMessageTimeout = delegate { }; // Our custom timeout event

        private static Application _app = new Application("SMS Bridge");
        private static bool _isConnected = false;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private static readonly int _connectionTimeoutMs = 10000; // 10 seconds
        private static readonly ConcurrentDictionary<Guid, Timer> _messageTimers = new();
        private const int MESSAGE_TIMEOUT_MS = 630000; // 10.5 minutes
        private static readonly ConcurrentDictionary<Guid, (SmsStatus Status, DateTime SentAt, DateTime StatusAt)> _messageStatuses = new();

        private readonly Timer _connectionHealthCheckTimer;
        private const int CONNECTION_CHECK_INTERVAL_MS = 3600000; // Check connection every hour

        private readonly SmsReceivedHandler _smsReceivedHandler; // Instance of the new handler


        private void RefreshConnection(object? state)
        {
            try
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "ConnectionHealthCheck",
                    messageID: "",
                    details: $"Performing periodic connection refresh, current state: {_isConnected}"
                );

                // Force a reconnection regardless of current state
                _app.BeginConnect(true);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "ConnectionRefreshFailed",
                    messageID: "",
                    details: $"Failed to refresh connection: {ex.Message}"
                );
            }
        }


        public JustRemotePhoneSmsProvider()
        {
            _smsReceivedHandler = new SmsReceivedHandler("JustRemotePhone"); // Initialize the handler

            _app.ApplicationStateChanged += OnApplicationStateChanged;
            _app.Phone.SMSSendResult += OnSMSSendResult;
            _app.Phone.SMSReceived += OnSMSReceived;

            OnMessageTimeout += HandleMessageTimeout; // Setup our own custom timeout handler

            _app.BeginConnect(true);
            _connectionHealthCheckTimer = new Timer(RefreshConnection, null,
                CONNECTION_CHECK_INTERVAL_MS, CONNECTION_CHECK_INTERVAL_MS);
            // Removed LoadReceivedMessagesFromDisk();
        }


        // Removed SaveReceivedMessagesToDisk()
        // Removed LoadReceivedMessagesFromDisk()


        private void OnApplicationStateChanged(ApplicationState newState, ApplicationState oldState)
        {
            _isConnected = newState == ApplicationState.Connected;
            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "ApplicationStateChanged",
                messageID: "",
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
                messageID: "",
                details: $"Connection attempt timed out after {_connectionTimeoutMs}ms"
            );
            return false;
        }

        private void SetupMessageTimeout(Guid messageId, string[] numbers)
        {
            var timer = new Timer(_ =>
            {
                if (_messageTimers.TryRemove(messageId, out var timerToDispose))
                {
                    timerToDispose.Dispose();
                    OnMessageTimeout?.Invoke(messageId, numbers);
                }
            }, null, MESSAGE_TIMEOUT_MS, Timeout.Infinite);

            _messageTimers[messageId] = timer;
        }


        private void HandleMessageTimeout(Guid messageId, string[] numbers)
        {
            if (_messageStatuses.TryGetValue(messageId, out var existing) && existing.StatusAt == DateTime.MinValue)
            {
                var timeoutInterval = DateTime.Now - existing.SentAt;
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "Timeout",
                    messageID: messageId.ToString(),
                    details: $"Message timed out after {timeoutInterval.TotalMinutes:F1} minutes. Numbers: {string.Join(",", numbers)}"
                );
                _messageStatuses[messageId] = (SmsStatus.TimedOut, existing.SentAt, DateTime.Now);
            }
        }


        private void OnSMSSendResult(Guid smsSendRequestId, string[] numbers, SMSSentResult[] results)
        {
            if (_messageStatuses.TryGetValue(smsSendRequestId, out var existing))
            {
                var now = DateTime.Now;
                var status = results.FirstOrDefault() switch
                {
                    SMSSentResult.Ok => SmsStatus.Delivered,
                    SMSSentResult.ErrorGeneric => SmsStatus.Failed,
                    _ => SmsStatus.Unknown
                };

                _messageStatuses[smsSendRequestId] = (status, existing.SentAt, now);

                var deliveryInterval = now - existing.SentAt;
                for (int i = 0; i < numbers.Length; i++)
                {
                    Logger.LogInfo(
                        provider: SmsProviderType.JustRemotePhone,
                        eventType: "DeliveryStatus",
                        messageID: smsSendRequestId.ToString(),
                        details: $"Number: {numbers[i]}, Status: {status}, Delivery Time: {deliveryInterval.TotalSeconds:F1} seconds"
                    );
                }
            }
            else
            {
                Logger.LogWarning(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "UnexpectedDeliveryStatus",
                    messageID: smsSendRequestId.ToString(),
                    details: $"Received delivery status but no send time recorded. Numbers: {string.Join(",", numbers)}"
                );
                _messageStatuses[smsSendRequestId] = (SmsStatus.Unknown, DateTime.Now, DateTime.Now);
            }

            if (_messageTimers.TryRemove(smsSendRequestId, out var timer))
            {
                timer.Dispose();
            }
        }
        private void OnSMSReceived(string number, string contactLabel, string text)
        {
            // Delegate handling to the new handler
            _smsReceivedHandler.HandleSmsReceived(number, contactLabel, text);
        }


        public async Task<(IResult Result, Guid MessageId)> SendSms(SendSmsRequest request)
        {
            // Validation check
            if (request.PhoneNumber.Contains(',') || request.PhoneNumber.Contains(';'))
            {
                var result = Results.Problem(
                    detail: "This SMS bridge only supports sending to single numbers",
                    statusCode: 400,
                    title: "Invalid Request"
                );
                return (Result: result, MessageId: Guid.Empty);
            }

            // Connection check.
            // I've added the log because I think this is redundant and I'm going to monitor the log to see if it ever happens
            if (!_isConnected)
            {
                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendSMS",
                    messageID: "",
                    details: $"Starting to send an SMS but RemotePhone is not connected.  Attempting to Connect "
                );

                if (!await EnsureConnectedAsync())
                {
                    var result = Results.Problem(
                        detail: "Failed to connect to SMS service after timeout",
                        statusCode: 503,
                        title: "SMS Service Unavailable"
                    );
                    return (Result: result, MessageId: Guid.Empty);
                }
            }


            try
            {
                string[] numbers = { request.PhoneNumber };
                string text = request.Message;
                Guid sendSMSRequestId = Guid.Empty;

                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendAttempt",
                    messageID: "",
                    details: $"PhoneNumber: {request.PhoneNumber}, Message: {request.Message}"
                );

                _app.Phone.SendSMS(numbers, text, out sendSMSRequestId);
                _messageStatuses[sendSMSRequestId] = (SmsStatus.Pending, DateTime.Now, DateTime.MinValue);
                SetupMessageTimeout(sendSMSRequestId, numbers);

                Logger.LogInfo(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendSuccess",
                    messageID: sendSMSRequestId.ToString(),
                    details: $"PhoneNumber: {request.PhoneNumber}"
                );

                var result = Results.Ok(new Result(
                    Success: true,
                    Message: "SMS queued for sending"
                ));

                return (Result: result, MessageId: sendSMSRequestId);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "SendFailure",
                    messageID: "",
                    details: $"PhoneNumber: {request.PhoneNumber}, Error: {ex.Message}"
                );

                var errorResult = Results.Problem(
                    detail: "Failed to send SMS: " + ex.Message,
                    statusCode: 500,
                    title: "SMS Send Failure"
                );

                return (Result: errorResult, MessageId: Guid.Empty);
            }
        }

        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            // Delegate to the new handler
            return _smsReceivedHandler.GetReceivedMessages();
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            var now = DateTime.Now;
            var recentStatuses = _messageStatuses
                .Where(entry => (now - entry.Value.SentAt).TotalHours <= 24) // Example: Last 24 hours
                .Select(entry => new MessageStatusRecord(
                    MessageId: entry.Key,
                    Status: entry.Value.Status,
                    SentAt: entry.Value.SentAt,
                    StatusAt: entry.Value.StatusAt
                ))
                .ToList();

            return Task.FromResult((IEnumerable<MessageStatusRecord>)recentStatuses);

        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId)
        {
            // Delegate to the new handler
            return _smsReceivedHandler.DeleteReceivedMessage(messageId);
        }





        public Task<SmsStatus> GetMessageStatus(Guid messageId)
        {
            Logger.LogInfo(
                provider: SmsProviderType.JustRemotePhone,
                eventType: "StatusCheck",
                messageID: messageId.ToString(),
                details: $"Timer exists: {_messageTimers.ContainsKey(messageId)}, Status exists: {_messageStatuses.ContainsKey(messageId)}"
            );

            if (_messageStatuses.TryGetValue(messageId, out var messageData))
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
                messageID: messageId.ToString(),
                details: "Status check for unknown message ID"
            );

            return Task.FromResult(SmsStatus.Failed);
        }
    }
}
