using SMS_Bridge.Models;
using SMS_Bridge.Services;
using Xunit;
using static SMS_Bridge.Services.CallCentreWatchdog;

namespace SMS_Bridge.Tests;

public class CallCentreWatchdogTests
{
    private static readonly TimeZoneInfo Nz = TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");
    private static readonly PhoneStatusResponse Healthy = new(Up: true, Detail: "Call Centre: Connected, phone: Idle");

    private readonly FakeClock _clock = new() { Now = new DateTimeOffset(2026, 11, 11, 13, 31, 0, Nz.GetUtcOffset(new DateTime(2026, 11, 11))) };
    private Connections _connections = new(Calls: true, Texts: true);
    private string? _notConfirming;
    private DateTimeOffset? _lastConfirmedAt;
    private int _restarts;
    private readonly CallCentreWatchdog _watchdog;

    public CallCentreWatchdogTests()
    {
        _watchdog = new CallCentreWatchdog(
            () => _connections, () => _notConfirming, () => _lastConfirmedAt,
            () => { _restarts++; return Task.CompletedTask; },
            _clock);
    }

    private async Task At(TimeSpan sinceStart)
    {
        _clock.Now = _clock.Start + sinceStart;
        await _watchdog.CheckAsync();
    }

    [Fact]
    public async Task A_texting_connection_missing_for_2_minutes_restarts_Call_Centre()
    {
        _connections = new(Calls: true, Texts: false);
        await At(TimeSpan.Zero);
        await At(TimeSpan.FromSeconds(119));
        Assert.Equal(0, _restarts);
        Assert.Equal(Healthy, _watchdog.WithRestarts(Healthy));

        await At(TimeSpan.FromSeconds(120));
        Assert.Equal(1, _restarts);

        _connections = new(Calls: true, Texts: true);
        await At(TimeSpan.FromSeconds(150));
        Assert.Equal(new PhoneStatusResponse(true, "Call Centre: Connected, phone: Idle, restarted Call Centre at 13:33"),
            _watchdog.WithRestarts(Healthy));
    }

    [Fact]
    public async Task A_phone_with_neither_connection_is_not_restarted_for()
    {
        _connections = new(Calls: false, Texts: false);
        for (var minutes = 0; minutes <= 60; minutes += 5) await At(TimeSpan.FromMinutes(minutes));

        Assert.Equal(0, _restarts);
    }

    [Fact]
    public async Task Sends_going_unconfirmed_restart_Call_Centre()
    {
        _notConfirming = "phone accepted texts but confirmed none since 13:31";
        await At(TimeSpan.Zero);

        Assert.Equal(1, _restarts);
    }

    [Fact]
    public async Task Restarts_are_spaced_15_minutes_apart_and_stop_after_two_until_a_text_is_confirmed()
    {
        _connections = new(Calls: true, Texts: false);
        await At(TimeSpan.Zero);
        await At(TimeSpan.FromMinutes(2));
        Assert.Equal(1, _restarts);

        await At(TimeSpan.FromMinutes(4));
        await At(TimeSpan.FromMinutes(16));
        Assert.Equal(1, _restarts);
        Assert.False(_watchdog.WithRestarts(Healthy).Up);

        await At(TimeSpan.FromMinutes(17));
        Assert.Equal(2, _restarts);

        await At(TimeSpan.FromMinutes(19));
        await At(TimeSpan.FromMinutes(60));
        Assert.Equal(2, _restarts);
        Assert.Equal(new PhoneStatusResponse(false, "Call Centre: Connected, phone: Idle, restarted Call Centre 2 times without fixing it; restart the phone"),
            _watchdog.WithRestarts(Healthy));

        _lastConfirmedAt = _clock.Start + TimeSpan.FromMinutes(61);
        await At(TimeSpan.FromMinutes(62));
        await At(TimeSpan.FromMinutes(64));
        Assert.Equal(3, _restarts);
    }

    [Fact]
    public async Task A_restart_that_brings_both_connections_back_doesnt_use_up_the_limit()
    {
        for (var episode = 0; episode < 3; episode++)
        {
            var start = TimeSpan.FromHours(episode * 5);
            _connections = new(Calls: true, Texts: false);
            await At(start);
            await At(start + TimeSpan.FromMinutes(2));
            _connections = new(Calls: true, Texts: true);
            await At(start + TimeSpan.FromMinutes(3));
        }

        Assert.Equal(3, _restarts);
        Assert.True(_watchdog.WithRestarts(Healthy).Up);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Start { get; private set; }
        private DateTimeOffset _now;
        public DateTimeOffset Now
        {
            get => _now;
            set { if (Start == default) Start = value; _now = value; }
        }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
