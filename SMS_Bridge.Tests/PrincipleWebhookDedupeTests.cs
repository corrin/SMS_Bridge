using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

public class PrincipleWebhookDedupeTests : IDisposable
{
    private const string Secret = "test-webhook-secret";
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "sms-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly PrincipleWebhookService _webhook;

    public PrincipleWebhookDedupeTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BRIDGE_API_KEY"] = "bridge-key",
            ["Principle:Enabled"] = "true",
            ["Principle:API_KEY"] = "principle-key",
            ["Principle:WEBHOOK_SECRET"] = Secret,
        }).Build();
        var fileConfig = new SMS_Bridge.Services.Configuration(config);
        var health = new BridgeHealth();
        _webhook = new PrincipleWebhookService(
            fileConfig,
            new PrincipleApiClient(new HttpClient(), config, fileConfig),
            new PrincipleOutboundSmsStore(config, _storeDir),
            new SmsQueueService(new NoSendProvider(), config, health),
            health);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_message_queue_it_once()
    {
        var body = $$$"""
            {"eventType":"message.created","timestamp":"2026-11-09T09:00:00Z","data":{
              "id":"msg-{{{Guid.NewGuid():N}}}","body":"Appointment reminder","direction":"outbound","status":"pending",
              "patientId":"p1","practiceId":"pr1","patientPhoneNumber":"+6421000000","practicePhoneNumber":"+6421999999"}}
            """;

        using var start = new ManualResetEventSlim();
        var retries = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return Status(await _webhook.HandleAsync(SignedRequest(body)));
        })).ToList();
        start.Set();
        var statuses = await Task.WhenAll(retries);

        Assert.Equal(1, statuses.Count(s => s == "queued"));
        Assert.Equal(9, statuses.Count(s => s == "duplicate"));
    }

    private static HttpRequest SignedRequest(string body)
    {
        var timestamp = "1762678800";
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")))
            .ToLowerInvariant();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["X-Principle-Signature"] = signature;
        context.Request.Headers["X-Principle-Timestamp"] = timestamp;
        return context.Request;
    }

    private static string? Status(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return json.RootElement.GetProperty("status").GetString();
    }

    // The queue's timer would hand messages to the provider; these tests only count what gets queued.
    private sealed class NoSendProvider : ISmsProvider
    {
        public Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId) =>
            Task.FromResult((Results.Ok() as IResult, smsBridgeId));
        public Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId) => Task.FromResult(SmsStatus.Pending);
        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId) => null;
        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages() => Task.FromResult(Enumerable.Empty<ReceiveSmsRequest>());
        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses() => Task.FromResult(Enumerable.Empty<MessageStatusRecord>());
    }
}
