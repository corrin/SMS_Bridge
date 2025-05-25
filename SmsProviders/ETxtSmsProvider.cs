﻿using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;
using SMS_Bridge.Services; // Added using for SmsReceivedHandler
using System;
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
        // Parity event, not used for eTXT
        public event Action<Guid, string[]> OnMessageTimeout = delegate { };

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private const string BaseUrl = "https://api.etxtservice.co.nz/v1";
        private readonly SmsReceivedHandler _smsReceivedHandler; // Instance of the new handler
        private readonly IConfiguration _configuration;
        private readonly Configuration _fileConfiguration;


        public ETxtSmsProvider(HttpClient httpClient, string apiKey, string apiSecret, IConfiguration configuration, Configuration fileConfiguration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _smsReceivedHandler = new SmsReceivedHandler(SmsProviderType.ETxt); 
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _fileConfiguration = fileConfiguration ?? throw new ArgumentNullException(nameof(fileConfiguration));


            // Ensure eTXT webhook is registered on startup
            EnsureETxtWebhook(_configuration, _fileConfiguration).GetAwaiter().GetResult();
        }


        public async Task<(IResult Result, Guid MessageId)> SendSms(SendSmsRequest request)
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
                    var id = Guid.Parse(data.Messages[0].MessageId);
                    return (Ok(), id);
                }
                else
                {
                    // Handle unexpected response format
                    var err = "Unexpected response format from eTXT API.";
                    return (Problem(detail: err), Guid.Empty);
                }
            }
            else
            {
                var err = await resp.Content.ReadAsStringAsync();
                return (Problem(detail: err), Guid.Empty);
            }
        }

        public Task<SmsStatus> GetMessageStatus(Guid messageId)
        {
            // Mirror JustRemotePhone behavior: no status-by-list, only per-message GET
            return GetStatusInternal(messageId);
        }

        private async Task<SmsStatus> GetStatusInternal(Guid messageId)
        {
            var url = $"{BaseUrl}/messages/{messageId}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBasicAuthHeader(req);

            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return SmsStatus.Unknown;

            var st = await resp.Content.ReadFromJsonAsync<ETxtStatusResponse>();
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
            // Delegate to the new handler
            return _smsReceivedHandler.GetReceivedMessages();
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            // Not supported by eTXT API, but we could potentially track sent message statuses here if needed.
            // For now, keeping it as returning an empty list to match original behavior for this method.
            return Task.FromResult((IEnumerable<MessageStatusRecord>)new List<MessageStatusRecord>());
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId)
        {
            // Delegate to the new handler
            return _smsReceivedHandler.DeleteReceivedMessage(messageId);
        }

        private void AddBasicAuthHeader(HttpRequestMessage req)
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:{_apiSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        // Placeholder method to handle incoming webhooks from eTXT
        // The actual implementation will depend on the eTXT webhook payload structure
        public IResult HandleInboundWebhook(ETxtWebhookRequest request)
        {
            // Assuming ETxtWebhookRequest has properties like FromNumber, MessageText, etc.
            // You would parse the incoming request body into this model.
            if (request != null)
            {
                if (!string.IsNullOrEmpty(request.SourceAddress))
                {
                    if (request.ReplyContent == null)
                    {
                        // Log error and return BadRequest if ReplyContent is null
                        Logger.LogError(SmsProviderType.ETxt, "HandleInboundWebhook", "", "Received ETxt webhook request with null ReplyContent.");
                        return BadRequest("ReplyContent is missing.");
                    }
                    else
                    {
                        // Extract relevant information and pass it to the handler
                        // ReplyContent is guaranteed not null here
                        _smsReceivedHandler.HandleSmsReceived(request.SourceAddress, "", (string)request.ReplyContent); // ContactLabel is not available here, pass empty string.

                        return Ok(); // Indicate successful processing
                    }
                }
                else
                {
                    // Log a warning or error if SourceAddress is null or empty
                    Logger.LogWarning(SmsProviderType.ETxt, "HandleInboundWebhook", "", "Received ETxt webhook request with null or empty SourceAddress.");
                    return BadRequest("SourceAddress is missing."); // Indicate bad request
                }
            }
            else
            {
                return BadRequest("Invalid webhook request payload.");
            }
        }

        // Webhook registration logic moved from Program.cs
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
                Logger.LogInfo(SmsProviderType.ETxt, "WebhookRegister", "", "Retrieved existing webhooks.");

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
                        Logger.LogInfo(SmsProviderType.ETxt, "WebhookRegister", "", $"Existing webhook with ID {existingWebhook.Id} updated.");
                    }
                    else
                    {
                        Logger.LogInfo(SmsProviderType.ETxt, "WebhookRegister", "", "Webhook already up to date.");
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
                    Logger.LogInfo(SmsProviderType.ETxt, "WebhookRegister", "", "Webhook registration succeeded.");
                }
            }
            catch (HttpRequestException httpEx)
            {
                var errorMsg = httpEx.Message;
                if (httpEx.StatusCode.HasValue)
                {
                     errorMsg = $"{httpEx.StatusCode.Value} - {errorMsg}";
                }
                Logger.LogError(SmsProviderType.ETxt, "WebhookRegister", "", $"Webhook operation failed: {errorMsg}");
            }
            catch (Exception ex)
            {
                Logger.LogError(SmsProviderType.ETxt, "WebhookRegister", "", $"An error occurred during webhook operation: {ex.Message}");
            }
        }

        // Helper method for comparing webhooks moved from Program.cs
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

            // Compare headers case-insensitively
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

    // Response models
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

    // Placeholder model for incoming eTXT webhook requests
    // This needs to be updated based on the actual eTXT webhook payload
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

    // Helper class for deserializing webhook list response moved from Program.cs
    public class ETxtWebhookListResponse
    {
        [JsonPropertyName("pageData")]
        public List<ETxtWebhook>? PageData { get; set; }
    }

    // Helper class for deserializing individual webhook details moved from Program.cs
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