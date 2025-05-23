using System;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks; // Added for Task return types

namespace SMS_Bridge.Services
{
    public class SmsReceivedHandler
    {
        private readonly string _providerName; // Field to store the provider name
        private static DateTime _lastZeroMessagesLogTime = DateTime.Now;  // Used to throttle the "No messages found" log
        private static readonly ConcurrentDictionary<Guid, (ReceiveSmsRequest Sms, DateTime ReceivedAt)> _receivedMessages = new();
        private readonly object _saveLock = new object(); // for saving the received messages Dictionary
        private static readonly string ReceivedMessagesDirectory = @"\\OPENDENTAL\OD Letters\msg_guids\";
        private static readonly string ReceivedMessagesFilePath = Path.Combine(
            ReceivedMessagesDirectory,
            $"{Environment.MachineName}_received_sms.json"
        );

        public SmsReceivedHandler(string providerName) // Added providerName parameter
        {
            _providerName = providerName; // Store the provider name
            LoadReceivedMessagesFromDisk();
        }

        public void HandleSmsReceived(string number, string contactLabel, string text)
        {
            var messageID = Guid.NewGuid(); // Generate a new ID for logging and tracking
            // Log the incoming message
            Logger.LogInfo(
                provider: _providerName, // Use the provider name field
                eventType: "SMSReceived",
                messageID: messageID.ToString(),
                details: $"From: {number}, Contact: {contactLabel}, Message: {text}"
            );

            var receivedSms = new ReceiveSmsRequest(
                MessageID: messageID,
                FromNumber: number,
                MessageText: text,
                ReceivedAt: DateTime.Now
            );
            _receivedMessages.TryAdd(messageID, (receivedSms, DateTime.Now));

            SaveReceivedMessagesToDisk();
        }

        private void SaveReceivedMessagesToDisk()
        {
            lock (_saveLock)
            {
                try
                {
                    // Ensure the directory exists
                    Directory.CreateDirectory(ReceivedMessagesDirectory);

                    // Extract only the `Sms` objects for saving
                    var messages = _receivedMessages.Values
                        .Select(entry => entry.Sms)
                        .ToList();

                    var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(ReceivedMessagesFilePath, json);

                    Logger.LogInfo(
                        provider: _providerName, // Use the provider name field
                        eventType: "SaveReceivedMessages",
                        messageID: "",
                        details: $"Saved {_receivedMessages.Count} messages to {ReceivedMessagesFilePath}."
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: _providerName, // Use the provider name field
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
                    var messages = JsonSerializer.Deserialize<List<ReceiveSmsRequest>>(json);

                    if (messages != null)
                    {
                        foreach (var message in messages)
                        {
                            _receivedMessages[message.MessageID] = (message, message.ReceivedAt);
                        }

                        Logger.LogInfo(
                            provider: _providerName, // Use the provider name field
                            eventType: "LoadReceivedMessages",
                            messageID: "",
                            details: $"Loaded {messages.Count} messages from {ReceivedMessagesFilePath}."
                        );
                    }
                }
                else
                {
                    Logger.LogInfo(
                        provider: _providerName, // Use the provider name field
                        eventType: "LoadReceivedMessages",
                        messageID: "",
                        details: $"No saved messages found. Starting fresh at {ReceivedMessagesFilePath}."
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: _providerName, // Use the provider name field
                    eventType: "LoadReceivedMessagesFailed",
                    messageID: "",
                    details: $"Failed to load received messages: {ex.Message}"
                );
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
                        provider: _providerName, // Use the provider name field
                        eventType: "NoMessages",
                        messageID: "",
                        details: "No messages found for a whole hour."
                    );
                    _lastZeroMessagesLogTime = DateTime.Now;
                }
            }
            else
            {
                Logger.LogInfo(
                    provider: _providerName, // Use the provider name field
                    eventType: "MessagesFound",
                    messageID: "",
                    details: $"Found {_receivedMessages.Count} messages in queue." // Use _receivedMessages.Count for accuracy
                );

                // Dump the full contents of the messages
                foreach (var message in messages)
                {
                    Logger.LogInfo(
                        provider: _providerName, // Use the provider name field
                        eventType: "MessageDump",
                        messageID: message.MessageID.ToString(),
                        details: $"Full Message Details: {JsonSerializer.Serialize(message)}"
                    );
                }
            }

            return Task.FromResult((IEnumerable<ReceiveSmsRequest>)messages);
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
                provider: _providerName, // Use the provider name field
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
    }
}