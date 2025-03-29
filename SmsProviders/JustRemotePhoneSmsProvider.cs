using System;
using SMS_Bridge.Models;
using JustRemotePhone.RemotePhoneService;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Collections.Generic;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SMS_Bridge.SmsProviders
{


    public class JustRemotePhoneSmsProvider : ISmsProvider
    {
        public event Action<Guid, string[]> OnMessageTimeout = delegate { }; // Our custom timeout event
        private static DateTime _lastZeroMessagesLogTime = DateTime.Now;  // Used to throttle the "No messages found" log

        private static Application _app = new Application("SMS Bridge");
        private static bool _isConnected = false;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private static readonly int _connectionTimeoutMs = 10000; // 10 seconds
        private static readonly ConcurrentDictionary<Guid, Timer> _messageTimers = new();
        private const int MESSAGE_TIMEOUT_MS = 630000; // 10.5 minutes
        private static readonly ConcurrentDictionary<Guid, (SmsStatus Status, DateTime SentAt, DateTime StatusAt)> _messageStatuses = new();
        private static readonly ConcurrentDictionary<Guid, (ReceiveSmsRequest Sms, DateTime ReceivedAt)> _receivedMessages = new();
        private readonly object _saveLock = new object(); // for saving the received messages Dictionary
        private static readonly string ReceivedMessagesDirectory = @"\\OPENDENTAL\OD Letters\msg_guids\";
        private static readonly string ReceivedMessagesFilePath = Path.Combine(
            ReceivedMessagesDirectory,
            $"{Environment.MachineName}_received_sms.json"
        );


        public JustRemotePhoneSmsProvider()
        {
            _app.ApplicationStateChanged += OnApplicationStateChanged;
            _app.Phone.SMSSendResult += OnSMSSendResult;
            _app.Phone.SMSReceived += OnSMSReceived;

            OnMessageTimeout += HandleMessageTimeout; // Setup our own custom timeout handler

            _app.BeginConnect(true);
            LoadReceivedMessagesFromDisk();
        }
        
        private void SaveReceivedMessagesToDisk()
        {
            lock (_saveLock)
            {
                try
                {
                    
                    // Extract only the `Sms` objects for saving
                    var messages = _receivedMessages.Values
                        .Select(entry => entry.Sms)
                        .ToList();

                    var json = System.Text.Json.JsonSerializer.Serialize(messages, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(ReceivedMessagesFilePath, json);

                    Logger.LogInfo(
                        provider: "SMS_Bridge",
                        eventType: "SaveReceivedMessages",
                        messageID: "",
                        details: $"Saved {_receivedMessages.Count} messages to {ReceivedMessagesFilePath}."
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: "SMS_Bridge",
                        eventType: "SaveReceivedMessagesFailed",
                        messageID: "",
                        details: $"Failed to save received messages: {ex.Message}"
                    );
                }
            }
        }

        private void LoadReceivedMessagesFromDisk()
        {
            try
            {
                if (File.Exists(ReceivedMessagesFilePath))
                {
                    var json = File.ReadAllText(ReceivedMessagesFilePath);
                    var messages = System.Text.Json.JsonSerializer.Deserialize<List<ReceiveSmsRequest>>(json);

                    if (messages != null)
                    {
                        foreach (var message in messages)
                        {
                            _receivedMessages[message.MessageID] = (message, message.ReceivedAt);
                        }

                        Logger.LogInfo(
                            provider: "SMS_Bridge",
                            eventType: "LoadReceivedMessages",
                            messageID: "",
                            details: $"Loaded {messages.Count} messages from {ReceivedMessagesFilePath}."
                        );
                    }
                }
                else
                {
                    Logger.LogInfo(
                        provider: "SMS_Bridge",
                        eventType: "LoadReceivedMessages",
                        messageID: "",
                        details: $"No saved messages found. Starting fresh at {ReceivedMessagesFilePath}."
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: "SMS_Bridge",
                    eventType: "LoadReceivedMessagesFailed",
                    messageID: "",
                    details: $"Failed to load received messages: {ex.Message}"
                );
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

            _app.BeginConnect(true);

            // Retry loop for connection
            for (int i = 0; i < 10; i++) // 10 attempts, 2 seconds each
            {
                if (_isConnected) return true;
                await Task.Delay(2000);
            }

            Logger.LogError(
                provider: "JustRemotePhone",
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
                    provider: "JustRemotePhone",
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
                        provider: "JustRemotePhone",
                        eventType: "DeliveryStatus",
                        messageID: smsSendRequestId.ToString(),
                        details: $"Number: {numbers[i]}, Status: {status}, Delivery Time: {deliveryInterval.TotalSeconds:F1} seconds"
                    );
                }
            }
            else
            {
                Logger.LogWarning(
                    provider: "JustRemotePhone",
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
                ReceivedAt: DateTime.Now
            );
            _receivedMessages.TryAdd(messageID, (receivedSms, DateTime.Now));

            SaveReceivedMessagesToDisk();
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
                    provider: "JustRemotePhone",
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
                    provider: "JustRemotePhone",
                    eventType: "SendAttempt",
                    messageID: "",
                    details: $"PhoneNumber: {request.PhoneNumber}, Message: {request.Message}"
                );

                _app.Phone.SendSMS(numbers, text, out sendSMSRequestId);
                _messageStatuses[sendSMSRequestId] = (SmsStatus.Pending, DateTime.Now, DateTime.MinValue);
                SetupMessageTimeout(sendSMSRequestId, numbers);

                Logger.LogInfo(
                    provider: "JustRemotePhone",
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
                .Select(m => m.Sms)
                .ToList(); // Materialize for logging

            if (messages.Count == 0)
            {

                if ((DateTime.Now - _lastZeroMessagesLogTime).TotalMinutes > 60)
                {
                    Logger.LogInfo(
                        provider: "SMS_Bridge",
                        eventType: "NoMessages",
                        messageID: "",
                        details: "No messages found for a whole hour."
                    );
                    _lastZeroMessagesLogTime = DateTime.Now;
                }
            } else {
                Logger.LogInfo(
                    provider: "SMS_Bridge",
                    eventType: "MessagesFound",
                    messageID: "",
                    details: $"Found {messages.Count} messages in queue."
                );

                // Dump the full contents of the messages
                foreach (var message in messages)
                {
                    Logger.LogInfo(
                        provider: "SMS_Bridge",
                        eventType: "MessageDump",
                        messageID: message.MessageID.ToString(),
                        details: $"Full Message Details: {System.Text.Json.JsonSerializer.Serialize(message)}"
                    );
                }
            }

            return Task.FromResult((IEnumerable<ReceiveSmsRequest>)messages);
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
            bool isRemoved = _receivedMessages.TryRemove(messageId, out _);

            var response = new DeleteMessageResponse(
                MessageID: messageId.ToString(),
                Deleted: isRemoved,
                DeleteFeedback: isRemoved ? "Message deleted successfully" : "Message not found"
            );

            Logger.LogInfo(
                provider: "SMS_Bridge",
                eventType: isRemoved ? "MessageDeleted" : "MessageDeleteFailed",
                messageID: messageId.ToString(),
                details: isRemoved ? "Message successfully removed from the queue and saved to disk." : "Attempt to delete message failed. Message not found."
            );

                        if (isRemoved)
            {
                // Save the updated dictionary to disk
                SaveReceivedMessagesToDisk();
            }

            return Task.FromResult(response);
        }





        public Task<SmsStatus> GetMessageStatus(Guid messageId)
        {
            Logger.LogInfo(
                provider: "JustRemotePhone",
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
                provider: "JustRemotePhone",
                eventType: "UnknownMessageStatus",
                messageID: messageId.ToString(),
                details: "Status check for unknown message ID"
            );

            return Task.FromResult(SmsStatus.Failed);
        }
    }
}
