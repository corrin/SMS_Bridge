using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SMS_Bridge.Models;
using SMS_Bridge.Services;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

public class SmsQueueRetryTests : IDisposable
{
    private static readonly TimeZoneInfo Nz = TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "sms-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly PrincipleOutboundSmsStore _store;
    private readonly FakeClock _clock = new();
    private readonly FakeProvider _provider = new();
    private readonly FakePrinciple _principle = new();

    public SmsQueueRetryTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BRIDGE_API_KEY"] = "bridge-key",
            ["Principle:Enabled"] = "true",
            ["Principle:API_KEY"] = "principle-key",
            ["Principle:WEBHOOK_SECRET"] = "secret",
            ["QueueSettings:GiveUpAfterSendingHours"] = "4",
        }).Build();
        _store = new PrincipleOutboundSmsStore(_config, _storeDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
    }

    private SmsQueueService NewQueue() => new(
        _provider, _config, new BridgeHealth(), _store,
        new PrincipleApiClient(new HttpClient(_principle), _config, new SMS_Bridge.Services.Configuration(_config)),
        _clock);

    private static DateTimeOffset NzTime(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, Nz.GetUtcOffset(local));
    }

    private async Task<PrincipleOutboundSmsMapRecord> StoreText(DateTimeOffset receivedAt, string phone = "+6421000001",
        OutboundDelivery? delivery = null)
    {
        var record = new PrincipleOutboundSmsMapRecord(
            PrincipleMessageId: $"msg-{Guid.NewGuid():N}",
            SmsBridgeId: Guid.NewGuid().ToString(),
            PatientPhoneNumber: phone,
            PatientId: "patient-1",
            PracticeId: "practice-1",
            Body: "Appointment reminder",
            ReceivedAt: receivedAt,
            Delivery: delivery ?? new OutboundDelivery(OutboundSmsStatus.Pending));
        Assert.True(await _store.AddAsync(record));
        return record;
    }

    private async Task<OutboundDelivery> Delivery(PrincipleOutboundSmsMapRecord record)
    {
        var all = await _store.UnfinishedAsync();
        return all.Single(r => r.PrincipleMessageId == record.PrincipleMessageId).Delivery!;
    }

    private static SmsBridgeId IdOf(PrincipleOutboundSmsMapRecord record) => new(Guid.Parse(record.SmsBridgeId));

    // An outcome event is handled under the queue lock; the pass that follows waits for it.
    private async Task Outcome(SmsQueueService queue, PrincipleOutboundSmsMapRecord record, SmsStatus status)
    {
        _provider.Raise(IdOf(record), status);
        await queue.ProcessAsync();
    }

    [Fact]
    public async Task A_text_waits_while_the_phone_is_down_and_goes_out_when_it_returns()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        _provider.BlockedReason = "Call Centre: Connected, phone: Unknown";

        for (var i = 0; i < 3; i++)
        {
            _clock.Now += TimeSpan.FromMinutes(5);
            await queue.ProcessAsync();
        }

        Assert.Empty(_provider.Sends);
        var held = await Delivery(record);
        Assert.Equal(OutboundSmsStatus.Pending, held.Status);
        Assert.Equal(0, held.Attempts);
        Assert.Equal("Call Centre: Connected, phone: Unknown", held.LastError);

        _provider.BlockedReason = null;
        await queue.ProcessAsync();
        Assert.Single(_provider.Sends);
        Assert.Equal(OutboundSmsStatus.AwaitingResult, (await Delivery(record)).Status);

        await Outcome(queue, record, SmsStatus.Delivered);
        await queue.ReportAsync();

        Assert.Equal($"/v1/patients/patient-1/sms-messages/{record.PrincipleMessageId}/status sent", Assert.Single(_principle.Patches));
        Assert.DoesNotContain(await _store.UnfinishedAsync(), r => r.PrincipleMessageId == record.PrincipleMessageId);
    }

    [Fact]
    public async Task A_failed_send_is_retried_after_a_delay()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        _provider.NextStatusCodes.Enqueue(500);

        await queue.ProcessAsync();
        var failed = await Delivery(record);
        Assert.Equal(OutboundSmsStatus.Pending, failed.Status);
        Assert.Equal(1, failed.Attempts);

        _clock.Now += TimeSpan.FromSeconds(59);
        await queue.ProcessAsync();
        Assert.Single(_provider.Sends);

        _clock.Now += TimeSpan.FromSeconds(1);
        await queue.ProcessAsync();
        Assert.Equal(2, _provider.Sends.Count);
        Assert.Equal(OutboundSmsStatus.AwaitingResult, (await Delivery(record)).Status);
    }

    [Fact]
    public async Task A_send_the_phone_reports_failed_is_retried()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        await queue.ProcessAsync();

        await Outcome(queue, record, SmsStatus.Failed);

        var retry = await Delivery(record);
        Assert.Equal(OutboundSmsStatus.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal(_clock.Now + TimeSpan.FromMinutes(1), retry.NextAttemptAt);
    }

    [Fact]
    public async Task A_timed_out_send_is_resent_once_then_given_up_on()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        await queue.ProcessAsync();

        await Outcome(queue, record, SmsStatus.TimedOut);
        Assert.Equal(2, _provider.Sends.Count);

        await Outcome(queue, record, SmsStatus.TimedOut);
        await queue.ReportAsync();
        Assert.Equal(2, _provider.Sends.Count);
        Assert.Equal($"/v1/patients/patient-1/sms-messages/{record.PrincipleMessageId}/status failed", Assert.Single(_principle.Patches));
        var failed = Assert.Single(await _store.FailedAsync());
        Assert.Equal(record.PrincipleMessageId, failed.PrincipleMessageId);
    }

    [Fact]
    public async Task A_text_is_given_up_on_after_four_sending_hours()
    {
        _clock.Now = NzTime(2026, 11, 10, 14, 45);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        _provider.BlockedReason = "Call Centre: Connected, phone: Unknown";

        _clock.Now = NzTime(2026, 11, 10, 18, 44);
        await queue.ProcessAsync();
        Assert.Empty(await _store.FailedAsync());

        _clock.Now = NzTime(2026, 11, 10, 18, 46);
        await queue.ProcessAsync();

        var failed = Assert.Single(await _store.FailedAsync());
        Assert.Contains("phone: Unknown", failed.Reason);
        Assert.Equal(_clock.Now.UtcDateTime, failed.FailedAt);
        Assert.Equal(DateTimeKind.Utc, failed.ReceivedAt.Kind);
        Assert.Empty(_provider.Sends);
    }

    // 4 Apr 2027 NZDT ends (03:00 → 02:00); 27 Sep 2026 it starts (02:00 → 03:00).
    [Theory]
    [InlineData(2026, 11, 10)]
    [InlineData(2027, 4, 3)]
    [InlineData(2026, 9, 26)]
    public async Task A_backlog_that_clears_at_night_waits_until_8am_New_Zealand_time(int year, int month, int day)
    {
        _clock.Now = NzTime(year, month, day, 19, 30);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        _provider.BlockedReason = "Call Centre: Connected, phone: Unknown";
        await queue.ProcessAsync();

        var nextDay = new DateTime(year, month, day).AddDays(1);
        _provider.BlockedReason = null;
        _clock.Now = NzTime(nextDay.Year, nextDay.Month, nextDay.Day, 0, 57);
        await queue.ProcessAsync();
        _clock.Now = NzTime(nextDay.Year, nextDay.Month, nextDay.Day, 7, 59);
        await queue.ProcessAsync();
        Assert.Empty(_provider.Sends);

        _clock.Now = NzTime(nextDay.Year, nextDay.Month, nextDay.Day, 8, 0);
        await queue.ProcessAsync();
        Assert.Single(_provider.Sends);
    }

    [Fact]
    public async Task A_text_that_arrives_at_night_goes_straight_out()
    {
        _clock.Now = NzTime(2026, 11, 10, 21, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);

        await queue.ProcessAsync();

        Assert.Single(_provider.Sends);
    }

    [Fact]
    public async Task A_restart_resumes_stored_texts_and_resends_one_awaiting_the_phone_once()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var pending = await StoreText(_clock.Now);
        var awaiting = await StoreText(_clock.Now, delivery: new OutboundDelivery(OutboundSmsStatus.AwaitingResult));

        var queue = NewQueue();
        await queue.ProcessAsync();
        await queue.ProcessAsync();

        Assert.Equal(2, _provider.Sends.Count);
        Assert.Equal(1, (await Delivery(awaiting)).Timeouts);
        Assert.Equal(0, (await Delivery(pending)).Timeouts);
        var phone = new PhoneStatusResponse(Up: true, Detail: "Call Centre: Connected, phone: Idle");
        Assert.Equal(phone, queue.WithSends(phone));
    }

    [Fact]
    public async Task Records_stored_before_send_tracking_are_never_resent()
    {
        Directory.CreateDirectory(_storeDir);
        await File.WriteAllTextAsync(Path.Combine(_storeDir, $"{Environment.MachineName}_principle_sms_map.json"),
            """[{"PrincipleMessageId":"old","SmsBridgeId":"ec0e57ae-d707-45c7-8ff1-230556eecabc","PatientPhoneNumber":"+6421000001","PatientId":"p1","PracticeId":"pr1","Body":"Hello","ReceivedAt":"2026-11-10T12:16:38.6474878+13:00"}]""");

        var queue = NewQueue();
        await queue.ProcessAsync();

        Assert.Empty(_provider.Sends);
        Assert.Empty(await _store.UnfinishedAsync());
        Assert.NotNull(await _store.FindByPhoneNumberAsync("+6421000001"));
    }

    [Fact]
    public async Task A_failure_leaves_the_list_once_a_later_text_to_the_number_goes_out()
    {
        var failedAt = NzTime(2026, 11, 10, 18, 0);
        await StoreText(failedAt.AddHours(-5), "+6421000001",
            new OutboundDelivery(OutboundSmsStatus.Sent, FinishedAt: failedAt.AddHours(-1)));
        await StoreText(failedAt.AddHours(-4), "+6421000001",
            new OutboundDelivery(OutboundSmsStatus.Failed, FinishedAt: failedAt, LastError: "phone Unknown"));
        Assert.Single(await _store.FailedAsync());

        await StoreText(failedAt.AddHours(15), "+64 21 000 001",
            new OutboundDelivery(OutboundSmsStatus.Sent, FinishedAt: failedAt.AddHours(15)));

        Assert.Empty(await _store.FailedAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_report_Principle_fails_to_take_is_retried_after_a_delay_but_one_it_rejects_is_not(HttpStatusCode failure)
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now, delivery: new OutboundDelivery(OutboundSmsStatus.Sent, FinishedAt: _clock.Now));
        var queue = NewQueue();
        await queue.ProcessAsync();

        _principle.NextStatusCodes.Enqueue(failure);
        await queue.ReportAsync();
        Assert.False((await Delivery(record)).PrincipleReported);

        _clock.Now += TimeSpan.FromSeconds(59);
        await queue.ReportAsync();
        Assert.Single(_principle.Patches);

        _clock.Now += TimeSpan.FromSeconds(1);
        _principle.NextStatusCodes.Enqueue(HttpStatusCode.NotFound);
        await queue.ReportAsync();
        Assert.Empty(await _store.UnfinishedAsync());

        await queue.ReportAsync();
        Assert.Equal(2, _principle.Patches.Count);
    }

    [Fact]
    public async Task A_slow_Principle_doesnt_hold_up_sending()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        await StoreText(_clock.Now, delivery: new OutboundDelivery(OutboundSmsStatus.Sent, FinishedAt: _clock.Now));
        var waiting = await StoreText(_clock.Now.AddSeconds(1));
        var queue = NewQueue();
        await queue.ProcessAsync();
        _principle.Hang = new TaskCompletionSource();

        var reporting = queue.ReportAsync();
        _clock.Now += TimeSpan.FromSeconds(5);
        await queue.ProcessAsync();

        Assert.Contains(IdOf(waiting), _provider.Sends);
        _principle.Hang.SetResult();
        await reporting;
    }

    [Fact]
    public async Task A_text_the_provider_refuses_fails_at_once()
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var record = await StoreText(_clock.Now);
        var queue = NewQueue();
        queue.QueuePrinciple(record);
        _provider.NextStatusCodes.Enqueue(400);

        await queue.ProcessAsync();

        var failed = Assert.Single(await _store.FailedAsync());
        Assert.Equal("send failed with status 400", failed.Reason);
        Assert.Single(_provider.Sends);
    }

    [Fact]
    public async Task An_unreadable_store_doesnt_stop_other_texts_going_out()
    {
        Directory.CreateDirectory(_storeDir);
        await File.WriteAllTextAsync(Path.Combine(_storeDir, $"{Environment.MachineName}_principle_sms_map.json"), "[{\"truncated");
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var queue = NewQueue();

        queue.QueueSms(new SendSmsRequest("+6421000009", "Hello"));
        await queue.ProcessAsync();

        Assert.Single(_provider.Sends);
    }

    [Fact]
    public async Task With_nothing_left_to_send_the_phone_is_no_longer_reported_as_not_confirming()
    {
        var (queue, texts) = await QueueTexts(1);
        await queue.ProcessAsync();
        _clock.Now = NzTime(2026, 11, 10, 10, 11);
        await Outcome(queue, texts[0], SmsStatus.TimedOut);
        Assert.NotNull(queue.NotConfirming);

        _clock.Now = NzTime(2026, 11, 10, 10, 22);
        await Outcome(queue, texts[0], SmsStatus.TimedOut);

        Assert.Null(queue.NotConfirming);
    }

    private async Task<(SmsQueueService Queue, PrincipleOutboundSmsMapRecord[] Texts)> QueueTexts(int count)
    {
        _clock.Now = NzTime(2026, 11, 10, 10, 0);
        var texts = new PrincipleOutboundSmsMapRecord[count];
        for (var i = 0; i < count; i++)
            texts[i] = await StoreText(_clock.Now.AddSeconds(i - count), $"+642100000{i}");
        var queue = NewQueue();
        foreach (var text in texts) queue.QueuePrinciple(text);
        return (queue, texts);
    }

    [Theory]
    [InlineData(29, 2)]
    [InlineData(31, 1)]
    public async Task A_send_unconfirmed_for_30_seconds_holds_the_rest(int seconds, int sends)
    {
        var (queue, _) = await QueueTexts(2);
        await queue.ProcessAsync();

        _clock.Now += TimeSpan.FromSeconds(seconds);
        await queue.ProcessAsync();

        Assert.Equal(sends, _provider.Sends.Count);
    }

    [Fact]
    public async Task While_the_phone_confirms_nothing_one_test_text_goes_out_every_15_minutes()
    {
        var (queue, texts) = await QueueTexts(3);
        await queue.ProcessAsync();
        _clock.Now += TimeSpan.FromSeconds(31);
        await queue.ProcessAsync();
        Assert.Single(_provider.Sends);

        _clock.Now = NzTime(2026, 11, 10, 10, 11);
        await Outcome(queue, texts[0], SmsStatus.TimedOut);
        Assert.Equal(2, _provider.Sends.Count);
        Assert.Equal(IdOf(texts[0]), _provider.Sends[1]);

        _clock.Now = NzTime(2026, 11, 10, 10, 22);
        await Outcome(queue, texts[0], SmsStatus.TimedOut);
        _clock.Now = NzTime(2026, 11, 10, 10, 25);
        await queue.ProcessAsync();
        Assert.Equal(2, _provider.Sends.Count);

        _clock.Now = NzTime(2026, 11, 10, 10, 26);
        await queue.ProcessAsync();
        Assert.Equal(3, _provider.Sends.Count);
        Assert.Equal(IdOf(texts[1]), _provider.Sends[2]);

        await Outcome(queue, texts[1], SmsStatus.Delivered);
        Assert.Equal(4, _provider.Sends.Count);
        Assert.Equal(IdOf(texts[2]), _provider.Sends[3]);
        Assert.Equal(texts[0].PrincipleMessageId, Assert.Single(await _store.FailedAsync()).PrincipleMessageId);
    }

    [Fact]
    public async Task A_send_the_phone_reports_failed_shows_the_phone_is_answering()
    {
        var (queue, texts) = await QueueTexts(2);
        await queue.ProcessAsync();
        _clock.Now = NzTime(2026, 11, 10, 10, 11);
        await Outcome(queue, texts[0], SmsStatus.TimedOut);
        Assert.Equal(2, _provider.Sends.Count);

        await Outcome(queue, texts[0], SmsStatus.Failed);

        Assert.Equal(3, _provider.Sends.Count);
        Assert.Equal(IdOf(texts[1]), _provider.Sends[2]);
    }

    [Fact]
    public async Task Phone_status_reports_texts_going_unconfirmed_until_one_is_confirmed()
    {
        var phone = new PhoneStatusResponse(Up: true, Detail: "Call Centre: Connected, phone: Idle");
        var (queue, texts) = await QueueTexts(2);
        await queue.ProcessAsync();
        Assert.Equal(phone, queue.WithSends(phone));

        _clock.Now += TimeSpan.FromSeconds(31);
        await queue.ProcessAsync();
        Assert.Equal(new PhoneStatusResponse(false, "Call Centre: Connected, phone: Idle, texts: phone accepted texts but confirmed none since 10:00"),
            queue.WithSends(phone));
        Assert.Equal("phone accepted texts but confirmed none since 10:00", (await Delivery(texts[1])).LastError);

        await Outcome(queue, texts[0], SmsStatus.Delivered);
        Assert.Equal(phone, queue.WithSends(phone));
    }

    [Fact]
    public async Task After_Call_Centre_restarts_texts_it_was_given_are_resent_once_and_sending_resumes()
    {
        var (queue, texts) = await QueueTexts(2);
        await queue.ProcessAsync();
        _clock.Now += TimeSpan.FromSeconds(31);
        await queue.ProcessAsync();
        Assert.NotNull(queue.NotConfirming);

        var restarts = 0;
        await queue.RestartCallCentreAsync(() => restarts++);
        await queue.ProcessAsync();

        Assert.Equal(1, restarts);

        Assert.Null(queue.NotConfirming);
        Assert.Equal(1, (await Delivery(texts[0])).Timeouts);
        Assert.Equal(IdOf(texts[0]), _provider.Sends[1]);
    }

    [Fact]
    public void Sending_hours_count_only_8am_to_8pm_across_a_daylight_saving_change()
    {
        // 25 hours pass on 4 Apr 2027 (clocks go back); sending hours are still 12 a day.
        var from = NzTime(2027, 4, 3, 19, 0);
        var to = NzTime(2027, 4, 4, 9, 30);

        Assert.Equal(TimeSpan.FromHours(2.5), SendWindow.SendingTimeBetween(from, to));
        Assert.False(SendWindow.IsOpen(NzTime(2027, 4, 4, 7, 59)));
        Assert.True(SendWindow.IsOpen(NzTime(2027, 4, 4, 8, 0)));
        Assert.False(SendWindow.IsOpen(NzTime(2026, 9, 27, 20, 0)));
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;

        // Tests run each pass themselves.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider : ISmsProvider
    {
        public string? BlockedReason { get; set; }
        public Queue<int> NextStatusCodes { get; } = new();
        public List<SmsBridgeId> Sends { get; } = new();

        public event Action<SmsBridgeId, SmsStatus> SendOutcome = delegate { };
        public bool ReportsSendOutcome => true;
        public string? SendBlockedReason() => BlockedReason;
        public void Raise(SmsBridgeId id, SmsStatus status) => SendOutcome(id, status);

        public Task<(IResult Result, SmsBridgeId smsBridgeId)> SendSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            Sends.Add(smsBridgeId);
            var code = NextStatusCodes.Count > 0 ? NextStatusCodes.Dequeue() : 200;
            return Task.FromResult((Results.StatusCode(code), smsBridgeId));
        }

        public Task<SmsStatus> GetMessageStatus(SmsBridgeId smsBridgeId) => Task.FromResult(SmsStatus.Pending);
        public ProviderMessageId? GetProviderMessageID(SmsBridgeId smsBridgeId) => null;
        public Task<IEnumerable<ReceiveSmsRequest>> GetReceivedMessages() => Task.FromResult(Enumerable.Empty<ReceiveSmsRequest>());
        public Task<DeleteMessageResponse> DeleteReceivedMessage(SmsBridgeId smsBridgeId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageStatusRecord>> GetRecentMessageStatuses() => Task.FromResult(Enumerable.Empty<MessageStatusRecord>());
    }

    // Records each status update as "<path> <status>".
    private sealed class FakePrinciple : HttpMessageHandler
    {
        public Queue<HttpStatusCode> NextStatusCodes { get; } = new();
        public List<string> Patches { get; } = new();
        // When set, each request waits for it, as a Principle that doesn't answer.
        public TaskCompletionSource? Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("principle-key", request.Headers.GetValues("X-API-Key").Single());
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var status = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("status").GetString();
            Patches.Add($"{request.RequestUri!.AbsolutePath} {status}");
            if (Hang != null) await Hang.Task;
            return new HttpResponseMessage(NextStatusCodes.Count > 0 ? NextStatusCodes.Dequeue() : HttpStatusCode.OK);
        }
    }
}
