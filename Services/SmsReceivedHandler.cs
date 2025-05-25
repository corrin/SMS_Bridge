using SMS_Bridge.SmsProviders;
using System;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics.Tracing;

namespace SMS_Bridge.Services
{
    public class SmsReceivedHandler
    {
        private readonly SmsProviderType _SMSprovider; 
        private static DateTime _lastZeroMessagesLogTime = DateTime.Now;  // Used to throttle the "No messages found" log
        private static readonly ConcurrentDictionary<SmsBridgeId, (ReceiveSmsRequest Sms, DateTime ReceivedAt)> _receivedMessages = new();
        private readonly object _saveLock = new object(); // Prevents concurrent file access during save operations
        private static readonly string ReceivedMessagesDirectory = @"\\OPENDENTAL\OD Letters\msg_guids\";
        private static readonly string ReceivedMessagesFilePath = Path.Combine(
            ReceivedMessagesDirectory,
            $"{Environment.MachineName}_received_sms.json"
        );

        public SmsReceivedHandler(SmsProviderType provider) 
        {
            _SMSprovider = provider; 
            LoadReceivedMessagesFromDisk();
        }

        public void HandleSmsReceived(string number, string contactLabel, string text)
        {
            // FIXME: Set ProviderMessageID to the value from the provider
            var smsBridgeId = new SmsBridgeId(Guid.NewGuid());
            Logger.LogInfo(
                provider: _SMSprovider,
                eventType: "SMSReceived",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default, // How on earch don't we ahave a provider message after a receive? BUG BUG BUG
                details: $"From: {number}, Contact: {contactLabel}, Message: {text}"
            );

            var receivedSms = new ReceiveSmsRequest(
                MessageID: smsBridgeId,
                FromNumber: number,
                MessageText: text,
                ReceivedAt: DateTime.Now
            );
            _receivedMessages.TryAdd(smsBridgeId, (receivedSms, DateTime.Now));

            SaveReceivedMessagesToDisk();
        }

        private void SaveReceivedMessagesToDisk()
        {
            lock (_saveLock)
            {
                try
                {
                    // Create directory if it doesn't exist to prevent file write errors
                    Directory.CreateDirectory(ReceivedMessagesDirectory);

                    // Only save the message data, not the timestamps
                    var messages = _receivedMessages.Values
                        .Select(entry => entry.Sms)
                        .ToList();

                    var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(ReceivedMessagesFilePath, json);

                    Logger.LogInfo(
                        provider: _SMSprovider,
                        eventType: "SaveReceivedMessages",
                        SMSBridgeID: default,
                        providerMessageID: default,
                        details: $"Saved {_receivedMessages.Count} messages to {ReceivedMessagesFilePath}."
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        provider: _SMSprovider,
                        eventType: "SaveReceivedMessagesFailed",
                        SMSBridgeID: default,
                        providerMessageID: default,
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
                            provider: _SMSprovider,
                            eventType: "LoadReceivedMessages",
                            SMSBridgeID: default,
                            providerMessageID: default,
                            details: $"Loaded {messages.Count} messages from {ReceivedMessagesFilePath}."
                        );
                    }
                }
                else
                {
                    Logger.LogInfo(
                        provider: _SMSprovider,
                        eventType: "LoadReceivedMessages",
                        SMSBridgeID: default,
                        providerMessageID: default,
                        details: $"No saved messages found. Starting fresh at {ReceivedMessagesFilePath}."
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: _SMSprovider,
                    eventType: "LoadReceivedMessagesFailed",
                    SMSBridgeID: default,
                    providerMessageID: default,
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
                        provider: _SMSprovider,
                        eventType: "NoMessages",
                        SMSBridgeID: default,
                        providerMessageID: default,
                        details: "No messages found for a whole hour."
                    );
                    _lastZeroMessagesLogTime = DateTime.Now;
                }
            }
            else
            {
                Logger.LogInfo(
                    provider: _SMSprovider,
                    eventType: "MessagesFound",
                    SMSBridgeID: default,
                    providerMessageID: default,
                    details: $"Found {_receivedMessages.Count} messages in queue."
                );

                // Log complete message details for debugging purposes
                foreach (var message in messages)
                {
                    Logger.LogInfo(  // is this SMSBridgeID or ProviderMessageID
                        provider: _SMSprovider,
                        eventType: "MessageDump",
                        SMSBridgeID: message.MessageID,  // Is this a bug? Has provider been assigned to SMSBridgeID?
                        providerMessageID: default, // BUG.  You always have a provider Message ID on receive.
                        details: $"Full Message Details: {JsonSerializer.Serialize(message)}"
                    );
                }
            }
           return Task.FromResult((IEnumerable<ReceiveSmsRequest>)messages);
       }

       public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId)
       {
           bool isRemoved = _receivedMessages.TryRemove(smsBridgeId, out _);
           var response = new DeleteMessageResponse(
               SMSBridgeID: smsBridgeId,
               Deleted: isRemoved,
               DeleteFeedback: isRemoved ? "Message deleted successfully" : "Message not found"
           );

           Logger.LogInfo(
                provider: _SMSprovider,
                eventType: isRemoved ? "MessageDeleted" : "MessageDeleteFailed",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default, // Provider message ID is not available here
                details: isRemoved ? "Message successfully removed from the queue and saved to disk." : "Attempt to delete message failed. Message not found."
           );

           if (isRemoved)
           {
               // Persist changes to ensure deleted messages stay deleted after restart
               SaveReceivedMessagesToDisk();
           }

           return Task.FromResult(response);
       }
   }
}