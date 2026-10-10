using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using SMS_Bridge;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

public class ApiKeyFilterTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    // Mirrors the group layout in Program.cs: keyed /smsgateway, /test nested under it,
    // /smsgateway/webhooks as a separate unkeyed group.
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();

        var gateway = ApiKeyFilter.MapKeyedGroup(_app, ApiKey, SmsProviderType.JustRemotePhone);
        gateway.MapGet("/gateway-status", () => Results.Ok());
        gateway.MapGroup("/test").MapGet("/check-send-sms", () => Results.Ok());
        _app.MapGroup("/smsgateway/webhooks").MapPost("/principle", () => Results.Ok());

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? host = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (host != null) request.Headers.Host = host;
        if (key != null) request.Headers.Add("X-API-Key", key);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("LOCALHOST")]
    public async Task Spoofed_local_host_header_without_key_is_rejected(string host)
    {
        var response = await Send(HttpMethod.Get, "/smsgateway/gateway-status", host: host);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_key_is_rejected()
    {
        var response = await Send(HttpMethod.Get, "/smsgateway/gateway-status", key: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Correct_key_is_accepted()
    {
        var response = await Send(HttpMethod.Get, "/smsgateway/gateway-status", key: ApiKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost")]
    public async Task Test_endpoints_without_key_are_rejected(string? host)
    {
        var response = await Send(HttpMethod.Get, "/smsgateway/test/check-send-sms", host: host);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Principle_webhook_does_not_need_the_api_key()
    {
        var response = await Send(HttpMethod.Post, "/smsgateway/webhooks/principle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class DebugModeDefaultTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("install-settings.example.json")]
    public void Debug_mode_defaults_to_off(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "SMS_Bridge.sln")))
            dir = dir.Parent ?? throw new InvalidOperationException("Repo root not found");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, file)));
        Assert.False(json.RootElement.GetProperty("SmsSettings").GetProperty("EnableDebugMode").GetBoolean());
    }
}
