using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SMS_Bridge;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

// A clock the test moves by hand. Its timers never fire, so the test calls Evaluate itself.
internal class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFires();

    private class NeverFires : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class PhoneLinkMonitorTests
{
    private readonly ManualClock _clock = new();
    private readonly PhoneLinkMonitor _monitor;
    private readonly List<PhoneLinkOutage> _started = new();
    private readonly List<PhoneLinkOutage> _ended = new();

    public PhoneLinkMonitorTests()
    {
        _monitor = new PhoneLinkMonitor(_clock, canShowPopup: true);
        _monitor.OutageStarted += (outage, _) => _started.Add(outage);
        _monitor.OutageEnded += outage => _ended.Add(outage);
    }

    private void Up() => _monitor.Report(true, "Call Centre: Connected, phone: Idle");
    private void Down() => _monitor.Report(false, "Call Centre: Connected, phone: Unknown");

    private void Wait(double minutes)
    {
        _clock.Advance(TimeSpan.FromMinutes(minutes));
        _monitor.Evaluate();
    }

    [Fact]
    public void Down_under_two_minutes_is_not_an_outage()
    {
        Up();
        Down();
        Wait(1.9);
        Up();
        Wait(60);

        Assert.Empty(_started);
        Assert.Null(_monitor.GetStatus().Outage);
    }

    [Fact]
    public void Down_two_minutes_starts_one_outage_dated_from_the_drop()
    {
        Up();
        Wait(5);
        var droppedAt = _clock.GetUtcNow();
        Down();
        Wait(2);
        Wait(60);

        var outage = Assert.Single(_started);
        Assert.Equal(droppedAt, outage.Started);
        var status = _monitor.GetStatus();
        Assert.False(status.Up);
        Assert.Equal(droppedAt, status.Since);
        Assert.Equal(outage, status.Outage);
    }

    [Fact]
    public void Flapping_all_day_is_one_outage()
    {
        Up();
        Down();
        Wait(2);
        var outage = Assert.Single(_started);

        for (var i = 0; i < 20; i++)
        {
            Up();
            Wait(29);
            Down();
            Wait(5);
        }

        Assert.Single(_started);
        Assert.Empty(_ended);
        Assert.Equal(outage.Id, _monitor.GetStatus().Outage?.Id);
    }

    [Fact]
    public void Outage_ends_after_thirty_minutes_continuously_up()
    {
        Up();
        Down();
        Wait(2);
        Up();
        Wait(29.9);
        Assert.Empty(_ended);

        Wait(0.1);
        Assert.Equal(_started, _ended);
        Assert.Null(_monitor.GetStatus().Outage);
        Assert.True(_monitor.GetStatus().Up);
    }

    [Fact]
    public void A_later_drop_after_the_outage_ends_is_a_new_outage()
    {
        Down();
        Wait(2);
        Up();
        Wait(30);
        Down();
        Wait(2);

        Assert.Equal(2, _started.Count);
        Assert.NotEqual(_started[0].Id, _started[1].Id);
    }

    [Fact]
    public void A_bridge_that_never_connects_has_an_outage_from_startup()
    {
        var startedAt = _clock.GetUtcNow();
        Wait(2);

        var outage = Assert.Single(_started);
        Assert.Equal(startedAt, outage.Started);
    }

    [Fact]
    public void A_restart_starts_fresh()
    {
        Down();
        Wait(2);
        Assert.Single(_started);

        var restarted = new PhoneLinkMonitor(_clock, canShowPopup: true);
        var restartedStarts = new List<PhoneLinkOutage>();
        restarted.OutageStarted += (outage, _) => restartedStarts.Add(outage);
        var status = restarted.GetStatus();

        Assert.False(status.Up);
        Assert.Equal(_clock.GetUtcNow(), status.Since);
        Assert.Null(status.Outage);

        _clock.Advance(TimeSpan.FromMinutes(2));
        restarted.Evaluate();
        Assert.NotEqual(_started[0].Id, Assert.Single(restartedStarts).Id);
    }

    [Fact]
    public void Detail_changes_without_resetting_since()
    {
        Down();
        var since = _monitor.GetStatus().Since;
        Wait(1);
        _monitor.Report(false, "Call Centre: Disconnected, phone: Unknown");

        var status = _monitor.GetStatus();
        Assert.Equal(since, status.Since);
        Assert.Equal("Call Centre: Disconnected, phone: Unknown", status.Detail);
    }
}

public class PhoneStatusEndpointTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiKeyAuthentication(ApiKey, SmsProviderType.JustRemotePhone);
        builder.Services.AddSingleton(new PhoneLinkMonitor(_clock, canShowPopup: false));
        _app = builder.Build();
        _app.MapGroup("/smsgateway").MapPhoneStatus();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpResponseMessage> Get(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/smsgateway/phone-status");
        if (key != null) request.Headers.Add("X-API-Key", key);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Without_the_key_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("wrong")).StatusCode);
    }

    [Fact]
    public async Task With_the_key_returns_the_agreed_shape()
    {
        var droppedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(3));

        var response = await Get(ApiKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.False(root.GetProperty("up").GetBoolean());
        Assert.Equal(droppedAt, root.GetProperty("since").GetDateTimeOffset());
        Assert.Equal("Starting: not yet connected to Call Centre", root.GetProperty("detail").GetString());
        Assert.False(root.GetProperty("popup").GetBoolean());
        var outage = root.GetProperty("outage");
        Assert.NotEqual(Guid.Empty, outage.GetProperty("id").GetGuid());
        Assert.Equal(droppedAt, outage.GetProperty("started").GetDateTimeOffset());
    }

    [Fact]
    public async Task No_outage_serialises_as_null()
    {
        var response = await Get(ApiKey);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("outage").ValueKind);
    }
}
