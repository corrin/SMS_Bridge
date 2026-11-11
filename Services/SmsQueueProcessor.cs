using System.Collections.Concurrent;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    // Sends each text once the phone can take it, retries until it goes out, and gives up after
    // QueueSettings:GiveUpAfterSendingHours of sending hours. Principle texts are kept in
    // PrincipleOutboundSmsStore, so a restart resumes them, and their outcome is reported to Principle.
    // /send-sms texts are kept in memory only.
    public class SmsQueueService
    {
        private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };

        // A backlog that clears at night waits for sending hours; a text that arrives outside them,
        // when someone chose to send it, still goes straight out.
        private static readonly TimeSpan HeldAfter = TimeSpan.FromMinutes(15);

        // The phone counts as not confirming once a send waits this long for its result. While it
        // isn't confirming, one held text goes out every TestEvery to test it, and nothing else.
        private static readonly TimeSpan UnconfirmedAfter = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TestEvery = TimeSpan.FromMinutes(15);

        private readonly ISmsProvider _provider;
        private readonly SmsProviderType _providerType;
        private readonly BridgeHealth _health;
        private readonly PrincipleOutboundSmsStore _store;
        private readonly PrincipleApiClient _principleApi;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _giveUpAfter;
        private readonly ConcurrentDictionary<SmsBridgeId, ProviderMessageId> _smsbridgetoproviderid = new();
        private readonly ITimer _processTimer;

        // Every change to a job happens under this lock: the timer and the provider's outcome events both change jobs.
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<SmsBridgeId, Job> _jobs = new();
        // New texts wait here for the next pass, so queueing never waits on a send in progress.
        private readonly ConcurrentQueue<(SmsBridgeId Id, Job Job)> _incoming = new();
        private bool _loaded;
        private string? _heldBecause;
        private DateTimeOffset? _unconfirmedSince;
        private DateTimeOffset? _lastTestAt;
        // Read by phone-status without the queue lock, so a send in progress never delays it.
        private volatile string? _notConfirming;
        private long _lastConfirmedTicks;
        private int _processing;
        private int _reporting;
        private bool _loadFailing;

        private sealed class Job
        {
            public required SendSmsRequest Request { get; init; }
            public required DateTimeOffset ReceivedAt { get; init; }
            public required OutboundDelivery Delivery { get; set; }
            public PrincipleOutboundSmsMapRecord? Principle { get; init; }
            public int ReportAttempts { get; set; }
            public DateTimeOffset NextReportAt { get; set; } = DateTimeOffset.MinValue;
        }

        public SmsQueueService(
            ISmsProvider provider,
            IConfiguration configuration,
            BridgeHealth health,
            PrincipleOutboundSmsStore store,
            PrincipleApiClient principleApi,
            TimeProvider clock)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _health = health;
            _store = store;
            _principleApi = principleApi;
            _clock = clock;
            _giveUpAfter = TimeSpan.FromHours(configuration.GetValue<double?>("QueueSettings:GiveUpAfterSendingHours")
                ?? throw new InvalidOperationException("QueueSettings:GiveUpAfterSendingHours must be configured in appsettings.json"));

            _providerType = provider switch
            {
                JustRemotePhoneSmsProvider => SmsProviderType.JustRemotePhone,
                ETxtSmsProvider => SmsProviderType.ETxt,
                DiafaanSmsProvider => SmsProviderType.Diafaan,
                _ => SmsProviderType.BuggyCodeNeedsFixing // Fallback for unsupported providers
            };

            _provider.SendOutcome += (smsBridgeId, status) => _ = HandleOutcomeAsync(smsBridgeId, status);
            _processTimer = _clock.CreateTimer(_ =>
            {
                _ = ProcessAsync();
                _ = ReportAsync();
            }, null, ProcessInterval, ProcessInterval);

            Logger.LogInfo(
                provider: _providerType,
                eventType: "Initialization",
                SMSBridgeID: default,
                providerMessageID: default,
                details: "SMS Queue initialized");
        }

        public SmsBridgeId QueueSms(SendSmsRequest request) =>
            QueueSms(request, new SmsBridgeId(Guid.NewGuid()));

        public SmsBridgeId QueueSms(SendSmsRequest request, SmsBridgeId smsBridgeId)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            else
            {
                // Happy case handled below
            }

            AddJob(smsBridgeId, new Job
            {
                Request = request,
                ReceivedAt = _clock.GetUtcNow(),
                Delivery = new OutboundDelivery(OutboundSmsStatus.Pending)
            });

            Logger.LogInfo(
                provider: _providerType,
                eventType: "MessageQueued",
                SMSBridgeID: smsBridgeId,
                providerMessageID: default, // providerMessageID is not available when queuing
                details: $"SMS queued for {request.PhoneNumber}");

            return smsBridgeId;
        }

        // The record must already be in the store with a Pending delivery.
        public void QueuePrinciple(PrincipleOutboundSmsMapRecord record)
        {
            var smsBridgeId = new SmsBridgeId(Guid.Parse(record.SmsBridgeId));
            AddJob(smsBridgeId, JobFor(record));

            Logger.LogInfo(
                provider: _providerType,
                eventType: "MessageQueued",
                SMSBridgeID: smsBridgeId,
                details: $"SMS queued for {record.PatientPhoneNumber}");
        }

        private void AddJob(SmsBridgeId smsBridgeId, Job job) => _incoming.Enqueue((smsBridgeId, job));

        private static Job JobFor(PrincipleOutboundSmsMapRecord record) => new()
        {
            Request = new SendSmsRequest(
                PhoneNumber: record.PatientPhoneNumber,
                Message: record.Body,
                CallbackUrl: null,
                SenderId: record.PracticePhoneNumber),
            ReceivedAt = record.ReceivedAt,
            Delivery = record.Delivery!,
            Principle = record
        };

        // One pass of the queue: gives up on overdue texts and sends at most one text. Runs on the
        // timer; tests call it directly.
        public async Task ProcessAsync()
        {
            if (Interlocked.Exchange(ref _processing, 1) == 1)
                return;
            else
            {
                // Happy case handled below
            }

            try
            {
                await _lock.WaitAsync();
                try
                {
                    while (_incoming.TryDequeue(out var incoming)) _jobs.TryAdd(incoming.Id, incoming.Job);
                    if (!_loaded) await TryLoadUnfinishedAsync();
                    await SendDueAsync();
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: _providerType,
                    eventType: "QueueProcessingFailed",
                    details: $"SMS queue pass failed: {ex}");
            }
            finally
            {
                Volatile.Write(ref _processing, 0);
            }
        }

        // An unreadable store mustn't stop texts already in memory going out; loading is retried each pass.
        private async Task TryLoadUnfinishedAsync()
        {
            try
            {
                await LoadUnfinishedAsync();
                if (_loadFailing)
                {
                    Logger.LogInfo(
                        provider: _providerType,
                        eventType: "StoreLoadRecovered",
                        details: "Loaded the stored Principle texts");
                    _loadFailing = false;
                }
                else
                {
                    // Loaded first time
                }
            }
            catch (Exception ex)
            {
                if (!_loadFailing)
                {
                    Logger.LogError(
                        provider: _providerType,
                        eventType: "StoreLoadFailed",
                        details: $"Can't load stored Principle texts, retrying each pass; texts in memory still go out: {ex.Message}");
                    _loadFailing = true;
                }
                else
                {
                    // Already logged
                }
            }
        }

        private async Task LoadUnfinishedAsync()
        {
            foreach (var record in await _store.UnfinishedAsync())
            {
                var smsBridgeId = new SmsBridgeId(Guid.Parse(record.SmsBridgeId));
                if (!_jobs.TryAdd(smsBridgeId, JobFor(record)))
                    continue;
                else
                {
                    // Happy case handled below
                }

                // The phone's result for a send made before the restart can no longer arrive.
                if (record.Delivery!.Status == OutboundSmsStatus.AwaitingResult)
                {
                    await TimedOutAsync(smsBridgeId, _jobs[smsBridgeId]);
                }
                else
                {
                    // Pending or finished: the passes below take it from here
                }
            }
            _loaded = true;
        }

        private async Task SendDueAsync()
        {
            var now = _clock.GetUtcNow();
            var due = _jobs
                .Where(j => j.Value.Delivery.Status == OutboundSmsStatus.Pending
                         && (j.Value.Delivery.NextAttemptAt ?? DateTimeOffset.MinValue) <= now)
                .OrderBy(j => j.Value.ReceivedAt)
                .ToList();

            foreach (var (smsBridgeId, job) in due.ToList())
            {
                if (SendWindow.SendingTimeBetween(job.ReceivedAt, now) >= _giveUpAfter)
                {
                    await FinishAsync(smsBridgeId, job, OutboundSmsStatus.Failed,
                        $"not sent within {_giveUpAfter.TotalHours:0.#} sending hours; last problem: {job.Delivery.LastError ?? "none recorded"}");
                    due.RemoveAll(d => d.Key == smsBridgeId);
                }
                else
                {
                    // Still worth sending
                }
            }

            // With no text in flight or waiting there is nothing to test the phone with, so stop
            // claiming it isn't confirming; the next text tests it within 30 seconds.
            if (!_jobs.Values.Any(j => j.Delivery.Status is OutboundSmsStatus.Pending or OutboundSmsStatus.AwaitingResult))
            {
                _unconfirmedSince = null;
                _lastTestAt = null;
            }
            else
            {
                // Texts still waiting
            }

            var unconfirmedSince = UnconfirmedSince(now);
            var notConfirming = unconfirmedSince == null
                ? null
                : $"phone accepted texts but confirmed none since {SendWindow.PracticeTime(unconfirmedSince.Value):HH:mm}";
            _notConfirming = notConfirming;

            if (due.Count == 0)
                return;
            else
            {
                // Happy case handled below
            }

            var blocked = _provider.SendBlockedReason();
            var testSend = false;
            if (blocked == null && notConfirming != null)
            {
                testSend = !_jobs.Values.Any(j => j.Delivery.Status == OutboundSmsStatus.AwaitingResult)
                        && (_lastTestAt == null || now - _lastTestAt >= TestEvery);
                blocked = testSend ? null : notConfirming;
            }
            else
            {
                // Held for the phone link, or the phone is confirming sends
            }

            if (blocked != null)
            {
                if (_heldBecause != blocked)
                {
                    Logger.LogWarning(
                        provider: _providerType,
                        eventType: "SendingHeld",
                        details: $"Holding {due.Count} text(s) until the phone can send: {blocked}");
                    _heldBecause = blocked;
                }
                else
                {
                    // Already logged this hold
                }
                var changed = due.Where(d => d.Value.Delivery.LastError != blocked).Select(d => d.Value).ToList();
                foreach (var job in changed) job.Delivery = job.Delivery with { LastError = blocked };
                await _store.UpdateManyAsync(changed
                    .Where(job => job.Principle != null)
                    .Select(job => job.Principle! with { Delivery = job.Delivery })
                    .ToList());
                return;
            }
            else if (testSend)
            {
                // Still held: this one text tests whether the phone confirms again
            }
            else if (_heldBecause != null)
            {
                Logger.LogInfo(
                    provider: _providerType,
                    eventType: "SendingResumed",
                    details: $"Phone can send again after: {_heldBecause}");
                _heldBecause = null;
            }
            else
            {
                // Not held
            }

            var next = due.FirstOrDefault(d => SendWindow.IsOpen(now) || now - d.Value.ReceivedAt < HeldAfter);
            if (next.Value == null)
                return;
            else
            {
                // Happy case handled below
            }

            if (testSend)
            {
                Logger.LogInfo(
                    provider: _providerType,
                    eventType: "SendingTest",
                    SMSBridgeID: next.Key,
                    details: $"Sending one held text to test the phone ({notConfirming})");
                _lastTestAt = now;
            }
            else
            {
                // Ordinary send
            }

            await SendAsync(next.Key, next.Value);
        }

        // When the phone stopped confirming sends, or null while it confirms them. It normally
        // confirms within 2-5 seconds; a phone that accepts texts and never confirms them shows
        // Idle and Connected throughout, so this is the only sign.
        private DateTimeOffset? UnconfirmedSince(DateTimeOffset now)
        {
            var oldestUnconfirmed = _jobs.Values
                .Where(j => j.Delivery.Status == OutboundSmsStatus.AwaitingResult
                         && j.Delivery.SentAt is { } sentAt && now - sentAt > UnconfirmedAfter)
                .Select(j => j.Delivery.SentAt)
                .Min();
            if (_unconfirmedSince == null) return oldestUnconfirmed;
            else if (oldestUnconfirmed == null) return _unconfirmedSince;
            else return oldestUnconfirmed < _unconfirmedSince ? oldestUnconfirmed : _unconfirmedSince;
        }

        // Why sends look stuck, or null while the phone confirms them.
        public string? NotConfirming => _notConfirming;

        // When the phone last answered a send, either way.
        public DateTimeOffset? LastConfirmedAt =>
            Interlocked.Read(ref _lastConfirmedTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

        // Restarts Call Centre while holding the queue lock, so no text is handed to the new one
        // before the old one's texts are dealt with. A fresh Call Centre can't report on texts given
        // to the one it replaced: each is resent once, as after a timeout.
        public async Task RestartCallCentreAsync(Action restart)
        {
            await _lock.WaitAsync();
            try
            {
                restart();
                foreach (var (smsBridgeId, job) in _jobs.Where(j => j.Value.Delivery.Status == OutboundSmsStatus.AwaitingResult).ToList())
                {
                    await TimedOutAsync(smsBridgeId, job);
                }
                _unconfirmedSince = null;
                _lastTestAt = null;
                _notConfirming = null;
            }
            finally
            {
                _lock.Release();
            }
        }

        // Texts going nowhere mean texts aren't going out, however healthy the phone link looks.
        public PhoneStatusResponse WithSends(PhoneStatusResponse phone)
        {
            var notConfirming = _notConfirming;
            return notConfirming == null
                ? phone
                : new PhoneStatusResponse(Up: false, Detail: $"{phone.Detail}, texts: {notConfirming}");
        }

        private async Task SendAsync(SmsBridgeId smsBridgeId, Job job)
        {
            using var _ = _health.Track($"SMS send {smsBridgeId}");
            string? error;
            int? statusCode = null;
            try
            {
                var (result, _) = await _provider.SendSms(job.Request, smsBridgeId);
                statusCode = (result as IStatusCodeHttpResult)?.StatusCode;
                error = statusCode is { } code && code != 200 ? $"send failed with status {code}" : null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // A 4xx is the provider refusing this text (a 400 for a list of numbers, say), so
            // retrying can't help; staff see it at once.
            if (error != null && statusCode is >= 400 and < 500)
            {
                await FinishAsync(smsBridgeId, job, OutboundSmsStatus.Failed, error);
                return;
            }
            else if (error != null)
            {
                await SendFailedAsync(smsBridgeId, job, error);
                return;
            }
            else
            {
                // Happy case handled below
            }

            var providerMessageId = _provider.GetProviderMessageID(smsBridgeId);
            if (providerMessageId != null)
            {
                _smsbridgetoproviderid[smsBridgeId] = providerMessageId.Value;
            }
            else
            {
                // No provider message ID to map
            }

            Logger.LogInfo(
                provider: _providerType,
                eventType: "MessageSent",
                SMSBridgeID: smsBridgeId,
                providerMessageID: providerMessageId ?? default,
                details: $"Mapped to providerMessageID (SMSBridgeID): {(providerMessageId?.ToString() ?? "unknown")} ({smsBridgeId}), SMS sent to {job.Request.PhoneNumber}");

            if (_provider.ReportsSendOutcome)
            {
                await SaveAsync(smsBridgeId, job, job.Delivery with { Status = OutboundSmsStatus.AwaitingResult, SentAt = _clock.GetUtcNow() });
            }
            else
            {
                await FinishAsync(smsBridgeId, job, OutboundSmsStatus.Sent, null);
            }
        }

        private async Task HandleOutcomeAsync(SmsBridgeId smsBridgeId, SmsStatus status)
        {
            try
            {
                await _lock.WaitAsync();
                try
                {
                    if (!_jobs.TryGetValue(smsBridgeId, out var job) || job.Delivery.Status != OutboundSmsStatus.AwaitingResult)
                        return;
                    else
                    {
                        // Happy case handled below
                    }

                    if (status is SmsStatus.Delivered or SmsStatus.Failed)
                    {
                        // The phone answered, so it is confirming sends again
                        _unconfirmedSince = null;
                        _lastTestAt = null;
                        Interlocked.Exchange(ref _lastConfirmedTicks, _clock.GetUtcNow().UtcTicks);
                    }
                    else
                    {
                        _unconfirmedSince ??= job.Delivery.SentAt ?? _clock.GetUtcNow();
                    }

                    if (status == SmsStatus.Delivered)
                    {
                        await FinishAsync(smsBridgeId, job, OutboundSmsStatus.Sent, null);
                    }
                    else if (status == SmsStatus.Failed)
                    {
                        await SendFailedAsync(smsBridgeId, job, "the phone reported the send failed");
                    }
                    else
                    {
                        await TimedOutAsync(smsBridgeId, job);
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: _providerType,
                    eventType: "QueueProcessingFailed",
                    SMSBridgeID: smsBridgeId,
                    details: $"Handling send outcome {status} failed: {ex}");
            }
        }

        // The text didn't go out; try again later, sooner for the first failures.
        private async Task SendFailedAsync(SmsBridgeId smsBridgeId, Job job, string error)
        {
            var attempts = job.Delivery.Attempts + 1;
            var delay = RetryDelays[Math.Min(attempts, RetryDelays.Length) - 1];
            Logger.LogError(
                provider: _providerType,
                eventType: "SendFailed",
                SMSBridgeID: smsBridgeId,
                details: $"Failed to send SMS to {job.Request.PhoneNumber} (attempt {attempts}): {error}. Retrying in {delay.TotalMinutes:0} min");
            await SaveAsync(smsBridgeId, job, job.Delivery with
            {
                Status = OutboundSmsStatus.Pending,
                Attempts = attempts,
                NextAttemptAt = _clock.GetUtcNow() + delay,
                LastError = error
            });
        }

        // No result from the phone: the text may have gone out, so it is resent once only.
        private async Task TimedOutAsync(SmsBridgeId smsBridgeId, Job job)
        {
            if (job.Delivery.Timeouts >= 1)
            {
                await FinishAsync(smsBridgeId, job, OutboundSmsStatus.Failed, "no send result from the phone, twice");
                return;
            }
            else
            {
                // Happy case handled below
            }

            Logger.LogWarning(
                provider: _providerType,
                eventType: "SendRetryAfterTimeout",
                SMSBridgeID: smsBridgeId,
                details: $"No send result from the phone for {job.Request.PhoneNumber}; resending once");
            await SaveAsync(smsBridgeId, job, job.Delivery with
            {
                Status = OutboundSmsStatus.Pending,
                Timeouts = job.Delivery.Timeouts + 1,
                NextAttemptAt = null,
                LastError = "no send result from the phone"
            });
        }

        private async Task FinishAsync(SmsBridgeId smsBridgeId, Job job, OutboundSmsStatus status, string? reason)
        {
            if (status == OutboundSmsStatus.Failed)
            {
                Logger.LogError(
                    provider: _providerType,
                    eventType: "SendGaveUp",
                    SMSBridgeID: smsBridgeId,
                    details: $"Gave up on SMS to {job.Request.PhoneNumber} (patient {job.Principle?.PatientId ?? "none"}, Principle message {job.Principle?.PrincipleMessageId ?? "none"}): {reason}");
            }
            else
            {
                // Sent: logged when handed to the phone and by the phone's result
            }

            await SaveAsync(smsBridgeId, job, job.Delivery with
            {
                Status = status,
                FinishedAt = _clock.GetUtcNow(),
                NextAttemptAt = null,
                LastError = reason ?? job.Delivery.LastError
            });
        }

        private async Task SaveAsync(SmsBridgeId smsBridgeId, Job job, OutboundDelivery delivery)
        {
            job.Delivery = delivery;
            if (job.Principle != null)
            {
                await _store.UpdateAsync(job.Principle with { Delivery = delivery });
            }
            else
            {
                // /send-sms texts live in memory only
            }

            if ((delivery.Status is OutboundSmsStatus.Sent or OutboundSmsStatus.Failed) && job.Principle == null)
            {
                _jobs.Remove(smsBridgeId);
            }
            else
            {
                // Still in progress, or a Principle text awaiting its report
            }
        }

        // Sets finished Principle texts' status in Principle. Runs on the timer apart from sending,
        // so a slow or down Principle never holds up texts; tests call it directly.
        public async Task ReportAsync()
        {
            if (Interlocked.Exchange(ref _reporting, 1) == 1)
                return;
            else
            {
                // Happy case handled below
            }

            try
            {
                await ReportToPrincipleAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: _providerType,
                    eventType: "PrincipleStatusPassFailed",
                    details: $"Principle status pass failed: {ex}");
            }
            finally
            {
                Volatile.Write(ref _reporting, 0);
            }
        }

        // Principle answers these for a request that can never succeed; anything else (auth, rate
        // limits, timeouts, server errors) is retried with the send retry delays.
        private static readonly HashSet<int> FinalRejections = new() { 400, 404, 409, 422 };

        private async Task ReportToPrincipleAsync()
        {
            List<(SmsBridgeId Id, Job Job)> finished;
            var now = _clock.GetUtcNow();
            await _lock.WaitAsync();
            try
            {
                finished = _jobs
                    .Where(j => j.Value.Principle != null
                             && (j.Value.Delivery.Status is OutboundSmsStatus.Sent or OutboundSmsStatus.Failed)
                             && j.Value.NextReportAt <= now)
                    .Select(j => (j.Key, j.Value))
                    .ToList();
            }
            finally
            {
                _lock.Release();
            }

            foreach (var (smsBridgeId, job) in finished)
            {
                var record = job.Principle!;
                var status = job.Delivery.Status == OutboundSmsStatus.Sent ? "sent" : "failed";
                bool done;
                if (record.PatientId == null)
                {
                    Logger.LogWarning(
                        provider: _providerType,
                        eventType: "PrincipleStatusSkipped",
                        SMSBridgeID: smsBridgeId,
                        details: $"Principle message {record.PrincipleMessageId} has no patient id, so its status ({status}) can't be set");
                    done = true;
                }
                else
                {
                    try
                    {
                        await _principleApi.UpdateSmsMessageStatusAsync(record.PatientId, record.PrincipleMessageId, status);
                        Logger.LogInfo(
                            provider: _providerType,
                            eventType: "PrincipleStatusReported",
                            SMSBridgeID: smsBridgeId,
                            details: $"Principle message {record.PrincipleMessageId} marked {status}");
                        done = true;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is { } code && FinalRejections.Contains((int)code))
                    {
                        Logger.LogError(
                            provider: _providerType,
                            eventType: "PrincipleStatusRejected",
                            SMSBridgeID: smsBridgeId,
                            details: $"Principle rejected marking message {record.PrincipleMessageId} {status}: {ex.Message}");
                        done = true;
                    }
                    catch (Exception ex)
                    {
                        job.ReportAttempts++;
                        var delay = RetryDelays[Math.Min(job.ReportAttempts, RetryDelays.Length) - 1];
                        job.NextReportAt = _clock.GetUtcNow() + delay;
                        Logger.LogWarning(
                            provider: _providerType,
                            eventType: "PrincipleStatusReportFailed",
                            SMSBridgeID: smsBridgeId,
                            details: $"Couldn't mark Principle message {record.PrincipleMessageId} {status}, retrying in {delay.TotalMinutes:0} min: {ex.Message}");
                        done = false;
                    }
                }

                if (done)
                {
                    await _lock.WaitAsync();
                    try
                    {
                        await _store.UpdateAsync(record with { Delivery = job.Delivery with { PrincipleReported = true } });
                        _jobs.Remove(smsBridgeId);
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
                else
                {
                    // Retried at NextReportAt
                }
            }
        }

        public bool TryGetProviderMessageID(Guid smsBridgeIdGuid, out Guid providerMessageIdGuid)
        {
            var smsBridgeId = new SmsBridgeId(smsBridgeIdGuid);
            providerMessageIdGuid = Guid.Empty;

            if (_smsbridgetoproviderid.TryGetValue(smsBridgeId, out var providerMessageId))
            {
                providerMessageIdGuid = providerMessageId.Value;
                return true;
            }
            else
            {
                // Happy case handled below
            }

            return false;
        }
    }
}
