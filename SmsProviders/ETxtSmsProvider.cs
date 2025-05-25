﻿using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;
using SMS_Bridge.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static Microsoft.AspNetCore.Http.Results;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace SMS_Bridge.SmsProviders
{
    public class ETxtSmsProvider : ISmsProvider
    {
        // Event included for interface parity, not needed for eTXT implementation
        public event Action<Guid, string[]> OnMessageTimeout = delegate { };

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private const string BaseUrl = "https://api.etxtservice.co.nz/v1";
        private readonly SmsReceivedHandler _smsReceivedHandler;
        private readonly IConfiguration _configuration;
        private readonly Configuration _fileConfiguration;
        private readonly ConcurrentDictionary<SmsBridgeId, ProviderMessageId> _smsBridgeToProviderId = new();


        public ETxtSmsProvider(HttpClient httpClient, string apiKey, string apiSecret, IConfiguration configuration, Configuration fileConfiguration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _smsReceivedHandler = new SmsReceivedHandler(SmsProviderType.ETxt); 
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _fileConfiguration = fileConfiguration ?? throw new ArgumentNullException(nameof(fileConfiguration));


            // Register webhook on startup to ensure SMS replies can be received
            EnsureETxtWebhook(_configuration, _fileConfiguration).GetAwaiter().GetResult();
        }


        public async Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            var url = $"{BaseUrl}/messages";
            var payload = new
            {
                messages = new[]
                {
                    new
                    {
                        content            = request.Message,
                        destination_number = request.PhoneNumber,
                        format             = "SMS",
                        source_number      = request.SenderId,
                        delivery_report    = true,
                        callback_url       = request.CallbackUrl
                    }
                }
            };

            var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            AddBasicAuthHeader(httpReq);

            var resp = await _httpClient.SendAsync(httpReq);
            if (resp.IsSuccessStatusCode)
            {
                var data = await resp.Content.ReadFromJsonAsync<ETxtSendResponse>();
                if (data?.Messages != null && data.Messages.Count > 0)
                {
                    var providerMessageId = new ProviderMessageId(Guid.Parse(data.Messages[0].MessageId));
                    _smsBridgeToProviderId[smsBridgeId] = providerMessageId;
                    return (Ok(), smsBridgeId);
                }
                else
                {
                    // API returned success but with invalid data structure
                    var err = "Unexpected response format from eTXT API.";
                    return (Problem(detail: err), smsBridgeId);
                }
            }
            else
            {
                var err = await resp.Content.ReadAsStringAsync();
                return (Problem(detail: err), smsBridgeId);
            }
        }

        public Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId)
        {
            // Mirror JustRemotePhone behavior: no status-by-list, only per-message GET
            // Use the GetProviderMessageID method to get the provider message ID
            var providerMessageId = GetProviderMessageID(smsBridgeId);
            return GetStatusProviderID(providerMessageId, smsBridgeId);
        }

        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId)
        {
            if (_smsBridgeToProviderId.TryGetValue(smsBridgeId, out var providerMessageId))
            {
                return providerMessageId;
            }
            
            throw new KeyNotFoundException($"No provider message ID found for SMS bridge ID: {smsBridgeId}");
        }

        private async Task<SmsStatus> GetStatusSMSID(SmsBridgeId smsBridgeId)
        {
            var providerMessageId = GetProviderMessageID(smsBridgeId);
            return await GetStatusProviderID(providerMessageId, smsBridgeId);
        }


        private async Task<SmsStatus> GetStatusProviderID(ProviderMessageId? providerMessageId, SmsBridgeId smsBridgeId)
        {
            // Todo we need to call by provider ID, not SMSBridgeId
            if (providerMessageId == null)
            {
                throw new ArgumentNullException(nameof(providerMessageId), "Provider message ID cannot be null");
            }
            
            var url = $"{BaseUrl}/messages/{providerMessageId}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBasicAuthHeader(req);

            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return SmsStatus.Unknown;

            var st = await resp.Content.ReadFromJsonAsync<ETxtStatusResponse>();
            if (st == null || string.IsNullOrEmpty(st.Status))
            {
                throw new InvalidOperationException("Invalid response from eTXT API: status is null or empty.");
            }
            return st.Status switch
            {
                "delivered" => SmsStatus.Delivered,
                "failed" => SmsStatus.Failed,
                "enroute" => SmsStatus.Pending,
                _ => SmsStatus.Unknown
            };
        }

        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages()
        {
            return _smsReceivedHandler.GetReceivedMessages();
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            // Not supported by eTXT API, but we could potentially track sent message statuses here if needed.
            // For now, keeping it as returning an empty list to match original behavior for this method.
            return Task.FromResult((IEnumerable<MessageStatusRecord>)new List<MessageStatusRecord>());
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId)
        {
            return _smsReceivedHandler.DeleteReceivedMessage(smsBridgeId);
        }

        private void AddBasicAuthHeader(HttpRequestMessage req)
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:{_apiSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        // Processes incoming webhook notifications from eTXT for received SMS messages
        public IResult HandleInboundWebhook(ETxtWebhookRequest request)
        {
            if (request == null)
            {
                return BadRequest("Invalid webhook request payload.");
            }

            // Handle possible none by checking upfront and raising an exception as per user instruction
            if (string.IsNullOrEmpty(request.MessageId))
            {
                 throw new InvalidOperationException("Received ETxt webhook request with null or empty MessageId.");
            }

            if (string.IsNullOrEmpty(request.SourceAddress))
            {
                Logger.LogWarning(provider: SmsProviderType.ETxt, eventType: "HandleInboundWebhook", details: "Received ETxt webhook request with null or empty SourceAddress.");
                return BadRequest("SourceAddress is missing."); // Indicate bad request
            }

            if (request.ReplyContent == null)
            {
                Logger.LogError(provider: SmsProviderType.ETxt, eventType: "HandleInboundWebhook", details: "Received ETxt webhook request with null ReplyContent.");
                return BadRequest("ReplyContent is missing.");
            }

            _smsReceivedHandler.HandleSmsReceived(
                number: request.SourceAddress,
                contactLabel: "", // ContactLabel is not available here, pass empty string
                text: (string)request.ReplyContent,
                providerMessageIdString: request.MessageId // Pass the provider's message ID
            );

            return Ok();
        }

        // Ensures the eTXT webhook is properly registered to receive SMS replies
        private async Task EnsureETxtWebhook(
            IConfiguration configuration,
            Configuration fileConfiguration)
        {
            var callbackKey = fileConfiguration.GetRequiredProviderSetting("etxt", "CALLBACK_KEY");

            var callbackBaseUrl = Environment.UserInteractive
             ? configuration["Hosting:AppBaseUrlDev"]!
             : configuration["Hosting:AppBaseUrlProd"]!;

            var apiBase = "https://api.etxtservice.co.nz/v1/webhooks/messages";
            var authHeader = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:{_apiSecret}"))
            );

            var desiredWebhook = new
            {
                url = $"{callbackBaseUrl}/smsgateway/receive-reply",
                method = "POST",
                encoding = "JSON",
                events = new[] { "RECEIVED_SMS" },
                headers = new Dictionary<string, string>
                { ["X-eTXT-Callback-Key"] = callbackKey },
                template = "{\"replyId\":\"$moId\",\"messageId\":\"$mtId\",\"replyContent\":\"$moContent\",\"sourceAddress\":\"$sourceAddress\",\"destinationAddress\":\"$destinationAddress\",\"timestamp\":\"$receivedTimestamp\"}",
                read_timeout = 5000,
                retries = 3,
                retry_delay = 30,
            };

            try
            {
                // Retrieve existing webhooks
                var getRequest = new HttpRequestMessage(HttpMethod.Get, apiBase);
                getRequest.Headers.Authorization = authHeader;

                var listResponse = await _httpClient.SendAsync(getRequest);
                listResponse.EnsureSuccessStatusCode();
                Logger.LogInfo(provider: SmsProviderType.ETxt,
                    eventType: "WebhookRegister",
                    details: "Retrieved existing webhooks.");

                var webhookListResponse = await listResponse.Content.ReadFromJsonAsync<ETxtWebhookListResponse>();
                var existingWebhook = webhookListResponse?.PageData?.FirstOrDefault(w => w.Url == desiredWebhook.url);

                if (existingWebhook != null)
                {
                    // Check if existing webhook needs update
                    if (WebhookNeedsUpdate(existingWebhook, desiredWebhook))
                    {
                        // Update existing webhook
                        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"{apiBase}/{existingWebhook.Id}")
                        {
                            Content = new StringContent(JsonSerializer.Serialize(desiredWebhook), Encoding.UTF8, "application/json")
                        };
                        putRequest.Headers.Authorization = authHeader;

                        var putResponse = await _httpClient.SendAsync(putRequest);
                        putResponse.EnsureSuccessStatusCode();
                        Logger.LogInfo(provider: SmsProviderType.ETxt,
                        eventType: "WebhookRegister",  details: $"Existing webhook with ID {existingWebhook.Id} updated.");
                    }
                    else
                    {
                        Logger.LogInfo(provider: SmsProviderType.ETxt, eventType: "WebhookRegister", details: "Webhook already up to date.");
                    }
                }
                else
                {
                    // Create new webhook
                    var postRequest = new HttpRequestMessage(HttpMethod.Post, apiBase)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(desiredWebhook), Encoding.UTF8, "application/json")
                    };
                    postRequest.Headers.Authorization = authHeader;

                    var postResponse = await _httpClient.SendAsync(postRequest);
                    postResponse.EnsureSuccessStatusCode();
                    Logger.LogInfo(provider: SmsProviderType.ETxt,
                        eventType: "WebhookRegister",
                        details: "Webhook registration succeeded.");
                }
            }
            catch (HttpRequestException httpEx)
            {
                var errorMsg = httpEx.Message;
                if (httpEx.StatusCode.HasValue)
                {
                     errorMsg = $"{httpEx.StatusCode.Value} - {errorMsg}";
                }
                Logger.LogError(SmsProviderType.ETxt, "WebhookRegister", $"Webhook operation failed: {errorMsg}");
            }
            catch (Exception ex)
            {
                Logger.LogError(SmsProviderType.ETxt, "WebhookRegister", $"An error occurred during webhook operation: {ex.Message}");
            }
        }

        // Determines if an existing webhook needs to be updated based on desired configuration
        private bool WebhookNeedsUpdate(ETxtWebhook existing, dynamic desired)
        {
            // Compare properties based on the Python example logic

            // Compare Events collections, handling nulls and ignoring order
            // This is stupidly and unnecessarily complex, stupid LLMs overcooking everything
            var existingEventsSet = new HashSet<string>(existing.Events ?? Enumerable.Empty<string>());
            var desiredEventsSet = new HashSet<string>(((IEnumerable<string>)desired.events) ?? Enumerable.Empty<string>());
            bool eventSetNeedsChange = !existingEventsSet.SetEquals(desiredEventsSet);

            if (existing.Method != desired.method ||
                existing.Encoding != desired.encoding ||
                eventSetNeedsChange ||
                existing.Template != desired.template ||
                existing.ReadTimeout != desired.read_timeout ||
                existing.Retries != desired.retries ||
                existing.RetryDelay != desired.retry_delay)
            {
                return true;
            }

            // Headers must be compared case-insensitively to avoid unnecessary updates
            var existingHeaders = existing.Headers?.ToDictionary(h => h.Key.ToLower(), h => h.Value) ?? new Dictionary<string, string>();
            var desiredHeaders = ((IDictionary<string, string>)desired.headers).ToDictionary(h => h.Key.ToLower(), h => h.Value);

            if (existingHeaders.Count != desiredHeaders.Count)
            {
                return true;
            }

            foreach (var pair in desiredHeaders)
            {
                if (!existingHeaders.TryGetValue(pair.Key, out var existingValue) || existingValue != pair.Value)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Models for eTXT API responses
    public class ETxtSendResponse
    {
        public List<ETxtMessageResponse>? Messages { get; set; }
    }

    public class ETxtMessageResponse
    {
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public class ETxtStatusResponse
    {
        public string? Status { get; set; }
    }

    // Model for incoming eTXT webhook requests that deliver SMS replies
    public class ETxtWebhookRequest
    {
        [JsonPropertyName("replyId")]
        public string? ReplyId { get; set; }

        [JsonPropertyName("messageId")]
        public string? MessageId { get; set; }

        [JsonPropertyName("replyContent")]
        public string? ReplyContent { get; set; }

        [JsonPropertyName("sourceAddress")]
        public string? SourceAddress { get; set; }

        [JsonPropertyName("destinationAddress")]
        public string? DestinationAddress { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }
    }

    // Helper class for deserializing webhook list response from eTXT API
    public class ETxtWebhookListResponse
    {
        [JsonPropertyName("pageData")]
        public List<ETxtWebhook>? PageData { get; set; }
    }

    // Helper class for deserializing individual webhook configuration details
    public class ETxtWebhook
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("method")]
        public string? Method { get; set; }
        public string? Encoding { get; set; }
        public List<string>? Events { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? Template { get; set; }
        [JsonPropertyName("read_timeout")]
        public int? ReadTimeout { get; set; }
        [JsonPropertyName("retries")]
        public int? Retries { get; set; }
        [JsonPropertyName("retry_delay")]
        public int? RetryDelay { get; set; }
    }
}