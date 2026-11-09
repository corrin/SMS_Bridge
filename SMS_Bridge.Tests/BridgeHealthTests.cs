using SMS_Bridge.Models;
using SMS_Bridge.Services;
using Xunit;

namespace SMS_Bridge.Tests;

public class BridgeHealthTests
{
    private static readonly PhoneStatusResponse PhoneUp = new(Up: true, Detail: "Call Centre: Connected, phone: Idle");

    private DateTime _now = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
    private readonly BridgeHealth _health;

    public BridgeHealthTests() => _health = new BridgeHealth(() => _now);

    [Fact]
    public void Operation_running_past_the_limit_turns_phone_status_down()
    {
        using var _ = _health.Track("inbound SMS from +6421000000");
        _now += BridgeHealth.StuckAfter + TimeSpan.FromSeconds(5);

        var status = _health.WithPhone(PhoneUp);

        Assert.False(status.Up);
        Assert.Equal($"{PhoneUp.Detail}, bridge: inbound SMS from +6421000000 stuck 125s", status.Detail);
    }

    [Fact]
    public void Operation_within_the_limit_leaves_phone_status_alone()
    {
        using var _ = _health.Track("Principle webhook");
        _now += BridgeHealth.StuckAfter - TimeSpan.FromSeconds(1);

        Assert.Equal(PhoneUp, _health.WithPhone(PhoneUp));
    }

    [Fact]
    public void Finished_operation_is_never_stuck()
    {
        _health.Track("Principle webhook").Dispose();
        _now += TimeSpan.FromHours(1);

        Assert.Equal(PhoneUp, _health.WithPhone(PhoneUp));
    }

    [Fact]
    public void Oldest_stuck_operation_is_reported()
    {
        using var older = _health.Track("SMS send A");
        _now += TimeSpan.FromMinutes(1);
        using var newer = _health.Track("Principle webhook");
        _now += TimeSpan.FromMinutes(5);

        Assert.Equal("SMS send A stuck 360s", _health.Stuck(BridgeHealth.StuckAfter));
    }

    [Fact]
    public void Phone_down_stays_down()
    {
        var phoneDown = new PhoneStatusResponse(Up: false, Detail: "Call Centre: Connecting, phone: Unknown");
        Assert.Equal(phoneDown, _health.WithPhone(phoneDown));
    }
}
