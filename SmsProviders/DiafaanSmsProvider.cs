﻿using SMS_Bridge.Models;
using SMS_Bridge.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static Microsoft.AspNetCore.Http.Results;
using Microsoft.Extensions.Configuration;

namespace SMS_Bridge.SmsProviders
{
    public class DiafaanSmsProvider : ISmsProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiUrl;
        private readonly string _apiUsername;
        private readonly string _apiPassword;
        private readonly ConcurrentDictionary<SmsBridgeId, ProviderMessageId> _smsBridgeToProviderId = new();
        private readonly ConcurrentDictionary<SmsBridgeId, (ProviderMessageId ProviderMessageID, SmsStatus Status, DateTime SentAt, DateTime StatusAt)> _messageStatuses = new();
        private readonly SmsReceivedHandler _smsReceivedHandler;
        
        public DiafaanSmsProvider(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _smsReceivedHandler = new SmsReceivedHandler(SmsProviderType.Diafaan);
            
            // Get configuration from appsettings.json
            _apiUrl = configuration["SmsSettings:Providers:diafaan:ApiUrl"] ?? "https://api.diafaan.com/";
            _apiUsername = configuration["SmsSettings:Providers:diafaan:Username"] ?? "default";
            _apiPassword = configuration["SmsSettings:Providers:diafaan:Password"] ?? "default";
            
            if (_apiUsername == "default" || _apiPassword == "default")
            {
                Logger.LogWarning(
                    provider: SmsProviderType.Diafaan,
                    eventType: "Configuration",
                    details: "Using default credentials. Please configure Diafaan API credentials in appsettings.json."
                );
            }
        }
        
        // Constructor for backward compatibility or testing
        public DiafaanSmsProvider()
        {
            _httpClient = new HttpClient();
            _smsReceivedHandler = new SmsReceivedHandler(SmsProviderType.Diafaan);
            _apiUrl = "DUMMY VALUE.  Check appsettings.json";
            _apiUsername = "default";
            _apiPassword = "default";
            
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "Configuration",
                details: "Using default constructor with placeholder credentials. This should only be used for testing."
            );
        }

        public async Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            try
            {
                // Prepare the request to Diafaan API
                var url = $"{_apiUrl.TrimEnd('/')}/send";
                var payload = new
                {
                    to = request.PhoneNumber,
                    message = request.Message,
                    sender = request.SenderId
                };

                var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                
                // Add authentication
                AddAuthHeader(httpReq);

                Logger.LogInfo(
                    provider: SmsProviderType.Diafaan,
                    eventType: "SendAttempt",
                    SMSBridgeID: smsBridgeId,
                    details: $"PhoneNumber: {request.PhoneNumber}, Message: {request.Message}"
                );

                // Send the request
                var response = await _httpClient.SendAsync(httpReq);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var responseData = JsonSerializer.Deserialize<DiafaanSendResponse>(responseContent);
                    
                    if (responseData?.Success == true && !string.IsNullOrEmpty(responseData.MessageId))
                    {
                        // Create a provider message ID from the response
                        var providerMessageId = new ProviderMessageId(Guid.Parse(responseData.MessageId));
                        
                        // Store the mapping between our internal ID and the provider's ID
                        _smsBridgeToProviderId[smsBridgeId] = providerMessageId;
                        
                        // Store the message status
                        _messageStatuses[smsBridgeId] = (providerMessageId, SmsStatus.Pending, DateTime.Now, DateTime.MinValue);
                        
                        Logger.LogInfo(
                            provider: SmsProviderType.Diafaan,
                            eventType: "SendSuccess",
                            SMSBridgeID: smsBridgeId,
                            providerMessageID: providerMessageId,
                            details: $"PhoneNumber: {request.PhoneNumber}"
                        );
                        
                        return (Ok(), smsBridgeId);
                    }
                    else
                    {
                        var errorMessage = responseData?.ErrorMessage ?? "Unknown error";
                        Logger.LogError(
                            provider: SmsProviderType.Diafaan,
                            eventType: "SendFailure",
                            SMSBridgeID: smsBridgeId,
                            details: $"Failed to send SMS: {errorMessage}"
                        );
                        
                        return (Problem(detail: errorMessage, statusCode: 500), smsBridgeId);
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.LogError(
                        provider: SmsProviderType.Diafaan,
                        eventType: "SendFailure",
                        SMSBridgeID: smsBridgeId,
                        details: $"API returned error: {response.StatusCode}, {errorContent}"
                    );
                    
                    return (Problem(
                        detail: $"Diafaan API error: {response.StatusCode}",
                        statusCode: (int)response.StatusCode
                    ), smsBridgeId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.Diafaan,
                    eventType: "SendException",
                    SMSBridgeID: smsBridgeId,
                    details: $"Exception sending SMS: {ex.Message}"
                );
                
                return (Problem(
                    detail: $"Exception sending SMS: {ex.Message}",
                    statusCode: 500
                ), smsBridgeId);
            }
        }

        public async Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId)
        {
            // First check if we have a cached status
            if (_messageStatuses.TryGetValue(smsBridgeId, out var cachedStatus))
            {
                // If the status is final (Delivered or Failed), return it immediately
                if (cachedStatus.Status == SmsStatus.Delivered || cachedStatus.Status == SmsStatus.Failed)
                {
                    return cachedStatus.Status;
                }
            }
            
            // Get the provider message ID
            var providerMessageId = GetProviderMessageID(smsBridgeId);
            if (providerMessageId == null)
            {
                Logger.LogWarning(
                    provider: SmsProviderType.Diafaan,
                    eventType: "StatusCheckFailed",
                    SMSBridgeID: smsBridgeId,
                    details: "No provider message ID found for this SMS bridge ID"
                );
                return SmsStatus.Unknown;
            }
            
            try
            {
                // Call Diafaan API to get the message status
                var url = $"{_apiUrl.TrimEnd('/')}/status/{providerMessageId.Value}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeader(request);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var statusResponse = JsonSerializer.Deserialize<DiafaanStatusResponse>(responseContent);
                    
                    if (statusResponse != null)
                    {
                        // Map Diafaan status to our status enum
                        var status = MapDiafaanStatus(statusResponse.Status);
                        
                        // Update the cached status
                        if (_messageStatuses.TryGetValue(smsBridgeId, out var existingStatus))
                        {
                            _messageStatuses[smsBridgeId] = (existingStatus.ProviderMessageID, status, existingStatus.SentAt, DateTime.Now);
                        }
                        
                        Logger.LogInfo(
                            provider: SmsProviderType.Diafaan,
                            eventType: "StatusCheck",
                            SMSBridgeID: smsBridgeId,
                            providerMessageID: providerMessageId.Value,
                            details: $"Status: {status}"
                        );
                        
                        return status;
                    }
                }
                
                Logger.LogWarning(
                    provider: SmsProviderType.Diafaan,
                    eventType: "StatusCheckFailed",
                    SMSBridgeID: smsBridgeId,
                    providerMessageID: providerMessageId.Value,
                    details: $"Failed to get status: {response.StatusCode}"
                );
                
                return SmsStatus.Unknown;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.Diafaan,
                    eventType: "StatusCheckException",
                    SMSBridgeID: smsBridgeId,
                    providerMessageID: providerMessageId.Value,
                    details: $"Exception checking status: {ex.Message}"
                );
                
                return SmsStatus.Unknown;
            }
        }

        public async Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            try
            {
                // Call Diafaan API to get received messages
                var url = $"{_apiUrl.TrimEnd('/')}/received";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeader(request);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var receivedMessages = JsonSerializer.Deserialize<DiafaanReceivedMessagesResponse>(responseContent);
                    
                    if (receivedMessages?.Messages != null && receivedMessages.Messages.Count > 0)
                    {
                        var result = new List<ReceiveSmsRequest>();
                        
                        foreach (var message in receivedMessages.Messages)
                        {
                            // Create a new SMS bridge ID for each received message
                            var smsBridgeId = new SmsBridgeId(Guid.NewGuid());
                            var providerMessageId = new ProviderMessageId(Guid.Parse(message.MessageId));
                            
                            // Parse the received timestamp
                            DateTime receivedAt;
                            if (!DateTime.TryParse(message.Timestamp, out receivedAt))
                            {
                                receivedAt = DateTime.Now;
                            }
                            
                            // Create a received SMS request
                            var receivedSms = new ReceiveSmsRequest(
                                MessageID: smsBridgeId,
                                ProviderMessageID: providerMessageId,
                                FromNumber: message.From,
                                MessageText: message.Text,
                                ReceivedAt: receivedAt
                            );
                            
                            // Process the received SMS through the handler
                            _smsReceivedHandler.HandleSmsReceived(
                                number: message.From,
                                contactLabel: "",
                                text: message.Text,
                                providerMessageIdString: message.MessageId
                            );
                            
                            result.Add(receivedSms);
                        }
                        
                        Logger.LogInfo(
                            provider: SmsProviderType.Diafaan,
                            eventType: "ReceivedMessages",
                            details: $"Retrieved {result.Count} messages from Diafaan API"
                        );
                        
                        return result;
                    }
                }
                
                // If we get here, either the API call failed or there were no messages
                return await _smsReceivedHandler.GetReceivedMessages();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.Diafaan,
                    eventType: "GetReceivedMessagesException",
                    details: $"Exception getting received messages: {ex.Message}"
                );
                
                // Fall back to the handler's cached messages
                return await _smsReceivedHandler.GetReceivedMessages();
            }
        }

        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId)
        {
            if (_smsBridgeToProviderId.TryGetValue(smsBridgeId, out var providerMessageId))
            {
                return providerMessageId;
            }
            
            Logger.LogWarning(
                provider: SmsProviderType.Diafaan,
                eventType: "UnknownProviderMessageID",
                SMSBridgeID: smsBridgeId,
                details: "Provider message ID lookup for unknown SMS bridge ID"
            );
            
            return null;
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId)
        {
            // Delegate to the handler for deleting received messages
            return _smsReceivedHandler.DeleteReceivedMessage(smsBridgeId);
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            var now = DateTime.Now;
            var recentStatuses = _messageStatuses
                .Where(entry => (now - entry.Value.SentAt).TotalHours <= 24) // Last 24 hours
                .Select(entry => new MessageStatusRecord(
                    SMSBridgeID: entry.Key,
                    ProviderMessageID: entry.Value.ProviderMessageID,
                    Status: entry.Value.Status,
                    SentAt: entry.Value.SentAt,
                    StatusAt: entry.Value.StatusAt == DateTime.MinValue ? entry.Value.SentAt : entry.Value.StatusAt
                ))
                .ToList();
            
            return Task.FromResult((IEnumerable<MessageStatusRecord>)recentStatuses);
        }
        
        private void AddAuthHeader(HttpRequestMessage request)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiUsername}:{_apiPassword}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        
        private SmsStatus MapDiafaanStatus(string diafaanStatus)
        {
            return diafaanStatus?.ToLower() switch
            {
                "delivered" => SmsStatus.Delivered,
                "sent" => SmsStatus.Delivered,
                "failed" => SmsStatus.Failed,
                "error" => SmsStatus.Failed,
                "pending" => SmsStatus.Pending,
                "queued" => SmsStatus.Pending,
                "sending" => SmsStatus.Pending,
                _ => SmsStatus.Unknown
            };
        }
    }
    
    // Models for Diafaan API responses
    public class DiafaanSendResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
        
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }
        
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }
    
    public class DiafaanStatusResponse
    {
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }
        
        [JsonPropertyName("status")]
        public string? Status { get; set; }
        
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }
    
    public class DiafaanReceivedMessagesResponse
    {
        [JsonPropertyName("messages")]
        public List<DiafaanReceivedMessage>? Messages { get; set; }
    }
    
    public class DiafaanReceivedMessage
    {
        [JsonPropertyName("message_id")]
        public string MessageId { get; set; } = "";
        
        [JsonPropertyName("from")]
        public string From { get; set; } = "";
        
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
        
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
    }
}
