using System;
using SMS_Bridge.Models;
using JustRemotePhone.RemotePhoneService;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace SMS_Bridge.SmsProviders
{
    public class JustRemotePhoneSmsProvider : ISmsProvider
    {
        public event Action<Guid, string[]> OnMessageTimeout = delegate { }; // Our custom timeout event

        private static Application? _app; // Set to non-null in the ctor
        private static bool _isConnected = false;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private static readonly int _connectionTimeoutMs = 10000; // 10 seconds
        private static readonly ConcurrentDictionary<Guid, Timer> _messageTimers = new();
        private const int MESSAGE_TIMEOUT_MS = 180000; // 3 minutes
        private static readonly ConcurrentDictionary<Guid, (SMSSentResult Result, DateTime CreatedAt)> _messageStatuses = new();
        private static readonly ConcurrentDictionary<Guid, (ReceiveSmsRequest Sms, DateTime ReceivedAt)> _receivedMessages = new();

        public JustRemotePhoneSmsProvider()
        {
            if (_app == null)
            {
                _app = new Application("SMS Bridge");
                _app.ApplicationStateChanged += OnApplicationStateChanged;
                _app.Phone.SMSSendResult += OnSMSSendResult;
                _app.Phone.SMSReceived += OnSMSReceived;

                OnMessageTimeout += HandleMessageTimeout; // Setup our own custom timeout handler

                _app.BeginConnect(true);
            }
        }

        private void OnApplicationStateChanged(ApplicationState newState, ApplicationState oldState)
        {
            _isConnected = newState == ApplicationState.Connected;
            Logger.LogInfo(
                provider: "JustRemotePhone",
                eventType: "ApplicationStateChanged",
                messageID: "",
                details: $"NewState: {newState}, OldState: {oldState}"
            );

        }

        private async Task<bool> EnsureConnectedAsync()
        {
            if (_isConnected) return true;
            if (_app == null) return false; // Constructor screwed up somehow

                await _connectionLock.WaitAsync();
            try
            {
                // Double-check pattern
                if (_isConnected) return true;

                Logger.LogInfo(
                    provider: "JustRemotePhone",
                    eventType: "ConnectionAttempt",
                    messageID: "",
                    details: "Attempting to connect..."
                );

                _app.BeginConnect(true);

                // Simple delay-based timeout
                for (int i = 0; i < 10; i++)  // 10 attempts, two seconds each
                {
                    if (_isConnected) return true;
                    await Task.Delay(2000);  // Wait 2 seconds between checks
                }

                Logger.LogError(
                    provider: "JustRemotePhone",
                    eventType: "ConnectionTimeout",
                    messageID: "",
                    details: $"Connection attempt timed out after {_connectionTimeoutMs}ms"
                );
                return false;
            }
            finally
            {
                _connectionLock.Release();
            }
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
            Logger.LogError(
                provider: "JustRemotePhone",
                eventType: "Timeout",
                messageID: messageId.ToString(),
                details: $"Message timed out after {MESSAGE_TIMEOUT_MS / 1000} seconds. Numbers: {string.Join(",", numbers)}"
            );
            _messageStatuses[messageId] = (SMSSentResult.ErrorGeneric, DateTime.UtcNow);

        }

        private void OnSMSSendResult(Guid smsSendRequestId, string[] numbers, SMSSentResult[] results)
        {
            if (_messageTimers.TryRemove(smsSendRequestId, out var timer))
            {
                timer.Dispose();
            }

            _messageStatuses[smsSendRequestId] = (results.FirstOrDefault(), DateTime.UtcNow);


            for (int i = 0; i < numbers.Length; i++)
            {
                var result = results[i];
                Logger.LogInfo(
                    provider: "JustRemotePhone",
                    eventType: "SMSSendResult",
                    messageID: smsSendRequestId.ToString(),
                    details: $"Number: {numbers[i]}, Status: {result}"
                );
            }
        }

        private void OnSMSReceived(string number, string contactLabel, string text)
        {
            var messageID = Guid.NewGuid(); // Generate a new ID for logging and tracking
            // Log the incoming message
            Logger.LogInfo(
                provider: "JustRemotePhone",
                eventType: "SMSReceived",
                messageID: messageID.ToString(), 
                details: $"From: {number}, Contact: {contactLabel}, Message: {text}"
            );

            // You might want to add additional logic here, such as storing the message in your database or triggering other processes.
            var receivedSms = new ReceiveSmsRequest(
                MessageID: messageID,
                FromNumber: number,
                MessageText: text,
                ReceivedAt: DateTime.UtcNow
            );
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

            // Connection check
            if (!await EnsureConnectedAsync())
            {
                var result = Results.Problem(
                    detail: "Failed to connect to SMS service after timeout",
                    statusCode: 503,
                    title: "SMS Service Unavailable"  // Fixed the title to match the status code
                );
                return (Result: result, MessageId: Guid.Empty);
            }

            try
            {
                string[] numbers = { request.PhoneNumber };
                string text = request.Message;
                Guid sendSMSRequestId = Guid.Empty;

                Logger.LogInfo(
                    provider: "JustRemotePhone",
                    eventType: "SendAttempt",
                    messageID: "",
                    details: $"PhoneNumber: {request.PhoneNumber}, Message: {request.Message}"
                );
                if (_app == null) throw new InvalidOperationException("SMS provider not properly initialized");

                // First create/queue the SMS
                _app.Phone.CreateSMS(numbers);

                _app.Phone.SendSMS(numbers, text, out sendSMSRequestId);
                SetupMessageTimeout(sendSMSRequestId, numbers);

                Logger.LogInfo(
                    provider: "JustRemotePhone",
                    eventType: "SendSuccess",
                    messageID: sendSMSRequestId.ToString(),
                    details: $"PhoneNumber: {request.PhoneNumber}"
                );

                var result = Results.Ok(new Result
                (
                    Success: true,
                    Message: "SMS queued for sending"
                ));

                return (Result: result, MessageId: sendSMSRequestId);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: "JustRemotePhone",
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
            var messages = _receivedMessages.Values
                .Select(m => m.Sms);

            _receivedMessages.Clear();
            return Task.FromResult(messages);
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId)
        {
            if (_receivedMessages.TryRemove(messageId, out _))
            {
                return Task.FromResult(new DeleteMessageResponse(
                    MessageID: messageId.ToString(),
                    Deleted: true,
                    DeleteFeedback: "Message deleted successfully"
                ));
            }

            return Task.FromResult(new DeleteMessageResponse(
                MessageID: messageId.ToString(),
                Deleted: false,
                DeleteFeedback: "Message not found"
            ));
        }

        public static int CleanupOldEntries(DateTime threshold)
        {
            int count = 0;

            foreach (var key in _messageStatuses.Keys)
            {
                if (_messageStatuses.TryGetValue(key, out var value) && value.CreatedAt < threshold)
                {
                    if (_messageStatuses.TryRemove(key, out _))
                    {
                        count++;
                    }
                }
            }

            foreach (var key in _receivedMessages.Keys)
            {
                if (_receivedMessages.TryGetValue(key, out var value) && value.ReceivedAt < threshold)
                {
                    if (_receivedMessages.TryRemove(key, out _))
                    {
                        count++;
                    }
                }
            }

            return count;
        }



        public Task<MessageStatus> GetMessageStatus(Guid messageId)
        {

            Logger.LogInfo(
                provider: "JustRemotePhone",
                eventType: "StatusCheck",
                messageID: messageId.ToString(),
                details: $"Timer exists: {_messageTimers.ContainsKey(messageId)}, Status exists: {_messageStatuses.ContainsKey(messageId)}"
            );

            // If we have an active timer, message is still pending
            if (_messageTimers.ContainsKey(messageId))
            {
                return Task.FromResult(MessageStatus.Pending);
            };

            // If we have a final status, map it to our simplified enum
            if (_messageStatuses.TryGetValue(messageId, out var messageData))
            {
                // messageData is now a tuple: (Result, CreatedAt)
                var justRemoteStatus = messageData.Result; // Extract the SMSSentResult
                return Task.FromResult(justRemoteStatus switch
                {
                    SMSSentResult.Ok => MessageStatus.Delivered,
                    _ => MessageStatus.Failed
                });
            }


            // If we don't know about this message at all, treat it as Failed
            Logger.LogWarning(
                provider: "JustRemotePhone",
                eventType: "UnknownMessageStatus",
                messageID: messageId.ToString(),
                details: "Status check for unknown message ID"
            );

            return Task.FromResult(MessageStatus.Failed);
        }
    }
}
