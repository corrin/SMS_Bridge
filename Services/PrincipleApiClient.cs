using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    public class PrincipleApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly PrincipleOptions _options;
        private readonly string _apiKey;

        public PrincipleApiClient(HttpClient httpClient, IConfiguration configuration, Configuration fileConfiguration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _options = configuration.GetSection("Principle").Get<PrincipleOptions>() ?? new PrincipleOptions();
            _options.ApiBaseUrl = _options.ApiBaseUrl.TrimEnd('/');
            _apiKey = fileConfiguration.GetSetting("PRINCIPLE_API_KEY") ?? "";

            if (_options.Enabled && string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("PRINCIPLE_API_KEY must be configured when Principle integration is enabled.");
            }
        }

        public bool Enabled => _options.Enabled;

        public IReadOnlyList<string> PracticeIds => _options.PracticeIds;

        public async Task<List<PrinciplePatient>> SearchPatientsByPhoneAsync(string practiceId, string phoneNumber)
        {
            var url = $"{_options.ApiBaseUrl}/v1/patients?practiceId={Uri.EscapeDataString(practiceId)}&phoneNumber={Uri.EscapeDataString(phoneNumber)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.PrinciplePatientSearchResponse);
            return result?.Data ?? new List<PrinciplePatient>();
        }

        public async Task CreateInboundSmsMessageAsync(string patientId, string body, string practiceId)
        {
            var url = $"{_options.ApiBaseUrl}/v1/patients/{Uri.EscapeDataString(patientId)}/sms-messages";
            var payload = new PrincipleSmsMessageCreate(
                Body: body,
                Direction: "inbound",
                Status: "sent",
                PracticeId: practiceId
            );
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, AppJsonSerializerContext.Default.PrincipleSmsMessageCreate),
                    Encoding.UTF8,
                    "application/json")
            };
            AddHeaders(request);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private void AddHeaders(HttpRequestMessage request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-API-Key", _apiKey);
        }
    }
}
