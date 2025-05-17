using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static Microsoft.AspNetCore.Http.Results;

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

        public ETxtSmsProvider(HttpClient httpClient, string apiKey, string apiSecret)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
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
                var id = Guid.Parse(data.Messages[0].MessageId);
                return (Ok(), id);
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
            // eTXT uses webhooks for inbound; polling unsupported
            return Task.FromResult((IEnumerable<ReceiveSmsRequest>)new List<ReceiveSmsRequest>());
        }

        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses()
        {
            // Not supported by eTXT
            return Task.FromResult((IEnumerable<MessageStatusRecord>)new List<MessageStatusRecord>());
        }

        public Task<DeleteMessageResponse> DeleteReceivedMessage(Guid messageId)
        {
            // Removal of received messages not supported by eTXT
            var resp = new DeleteMessageResponse(
                MessageID: messageId.ToString(),
                Deleted: false,
                DeleteFeedback: "Not supported by eTXT provider"
            );
            return Task.FromResult(resp);
        }

        private void AddBasicAuthHeader(HttpRequestMessage req)
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:{_apiSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    // Response models
    public class ETxtSendResponse
    {
        public List<ETxtMessageResponse> Messages { get; set; }
    }

    public class ETxtMessageResponse
    {
        [JsonPropertyName("message_id")]
        public string MessageId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }

    public class ETxtStatusResponse
    {
        public string Status { get; set; }
    }
}
