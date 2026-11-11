using System.Net.NetworkInformation;
using SMS_Bridge.Models;
using SMS_Bridge.SmsProviders;

namespace SMS_Bridge.Services
{
    // Call Centre keeps two TCP connections to the phone app: remote port 12331 carries calls and
    // the call log, 12321 carries texts, send results and phone-state events. After a network blip
    // it can rebuild 12331 but not 12321, while still reporting Connected and the phone Idle and
    // accepting texts it can't deliver. Restarting Call Centre rebuilds both, and doesn't drop a
    // call in progress.
    //
    // Restarts when that half-connection lasts HalfConnectedFor, or when the queue sees sends go
    // unconfirmed. At most once per RestartEvery, and at most MaxRestarts until a restart is seen to
    // work (a text confirmed, or both connections back after a half-connection restart), so a phone
    // that is truly broken gets a person instead of a restart loop. A phone that's off or off Wi-Fi
    // has neither connection, which isn't a half-connection.
    public class CallCentreWatchdog
    {
        public const int CallsPort = 12331;
        public const int TextsPort = 12321;
        private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HalfConnectedFor = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RestartEvery = TimeSpan.FromMinutes(15);
        private const int MaxRestarts = 2;
        // How long phone-status mentions a restart that worked.
        private static readonly TimeSpan NoteFor = TimeSpan.FromHours(1);

        public record Connections(bool Calls, bool Texts);

        private readonly Func<Connections> _connections;
        private readonly Func<string?> _notConfirming;
        private readonly Func<DateTimeOffset?> _lastConfirmedAt;
        private readonly Func<Task> _restart;
        private readonly TimeProvider _clock;
        private readonly ITimer _timer;
        private readonly SemaphoreSlim _checking = new(1, 1);

        private DateTimeOffset? _halfConnectedSince;
        private DateTimeOffset? _lastRestartAt;
        private int _restartsSinceConfirmed;
        private bool _lastRestartForHalfConnection;
        private bool _gaveUpLogged;
        // Read by phone-status without waiting for a check.
        private volatile PhoneNote? _note;

        private sealed record PhoneNote(bool Down, string Detail);

        public CallCentreWatchdog(
            Func<Connections> connections,
            Func<string?> notConfirming,
            Func<DateTimeOffset?> lastConfirmedAt,
            Func<Task> restart,
            TimeProvider clock)
        {
            _connections = connections;
            _notConfirming = notConfirming;
            _lastConfirmedAt = lastConfirmedAt;
            _restart = restart;
            _clock = clock;
            _timer = _clock.CreateTimer(_ => _ = CheckAsync(), null, CheckEvery, CheckEvery);
        }

        public static CallCentreWatchdog For(JustRemotePhoneSmsProvider provider, SmsQueueService queue, TimeProvider clock) =>
            new(PhoneConnections, () => queue.NotConfirming, () => queue.LastConfirmedAt,
                () => queue.RestartCallCentreAsync(provider.RestartCallCentre),
                clock);

        // Established connections to the phone's two ports, from any address, so a new DHCP lease
        // for the phone changes nothing.
        public static Connections PhoneConnections()
        {
            var established = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Where(c => c.State == TcpState.Established)
                .Select(c => c.RemoteEndPoint.Port)
                .ToHashSet();
            return new Connections(Calls: established.Contains(CallsPort), Texts: established.Contains(TextsPort));
        }

        // One check. Runs on the timer; tests call it directly.
        public async Task CheckAsync()
        {
            if (!await _checking.WaitAsync(0))
                return;
            else
            {
                // Happy case handled below
            }

            try
            {
                var now = _clock.GetUtcNow();
                var connections = _connections();
                var halfConnected = connections.Calls && !connections.Texts;
                _halfConnectedSince = halfConnected ? _halfConnectedSince ?? now : null;

                var restartWorked = _lastRestartAt != null
                    && (_lastConfirmedAt() > _lastRestartAt
                        || (_lastRestartForHalfConnection && connections.Calls && connections.Texts && _notConfirming() == null));
                if (restartWorked)
                {
                    _restartsSinceConfirmed = 0;
                    _gaveUpLogged = false;
                }
                else
                {
                    // No text confirmed since the last restart
                }

                var halfConnectedTooLong = halfConnected && now - _halfConnectedSince >= HalfConnectedFor;
                var trigger = halfConnectedTooLong
                    ? $"texting connection to the phone (port {TextsPort}) down since {SendWindow.PracticeTime(_halfConnectedSince!.Value):HH:mm}"
                    : _notConfirming();

                if (trigger != null && _restartsSinceConfirmed >= MaxRestarts)
                {
                    if (!_gaveUpLogged)
                    {
                        Logger.LogError(
                            provider: SmsProviderType.JustRemotePhone,
                            eventType: "CallCentreRestartGaveUp",
                            details: $"Restarted Call Centre {MaxRestarts} times without a text being confirmed; still {trigger}");
                        _gaveUpLogged = true;
                    }
                    else
                    {
                        // Already logged
                    }
                }
                else if (trigger != null && (_lastRestartAt == null || now - _lastRestartAt >= RestartEvery))
                {
                    Logger.LogWarning(
                        provider: SmsProviderType.JustRemotePhone,
                        eventType: "CallCentreRestart",
                        details: $"Restarting Call Centre: {trigger}");
                    _lastRestartAt = now;
                    _lastRestartForHalfConnection = halfConnectedTooLong;
                    _restartsSinceConfirmed++;
                    _halfConnectedSince = null;
                    halfConnectedTooLong = false;
                    await _restart();
                }
                else
                {
                    // Healthy, or waiting out the restart interval
                }

                _note = Note(now, trigger, halfConnectedTooLong);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    provider: SmsProviderType.JustRemotePhone,
                    eventType: "CallCentreWatchdogFailed",
                    details: $"Call Centre check failed: {ex}");
            }
            finally
            {
                _checking.Release();
            }
        }

        private PhoneNote? Note(DateTimeOffset now, string? trigger, bool halfConnectedTooLong)
        {
            var restarted = _lastRestartAt is { } restartedAt && now - restartedAt < NoteFor
                ? $"restarted Call Centre at {SendWindow.PracticeTime(restartedAt):HH:mm}"
                : null;
            if (trigger != null && _restartsSinceConfirmed >= MaxRestarts)
                return new PhoneNote(true, $"restarted Call Centre {MaxRestarts} times without fixing it; restart the phone");
            else if (halfConnectedTooLong)
                return new PhoneNote(true, restarted == null ? trigger! : $"{trigger}, {restarted}");
            else if (restarted != null)
                return new PhoneNote(false, restarted);
            else
                return null;
        }

        public PhoneStatusResponse WithRestarts(PhoneStatusResponse phone)
        {
            var note = _note;
            return note == null
                ? phone
                : new PhoneStatusResponse(Up: phone.Up && !note.Down, Detail: $"{phone.Detail}, {note.Detail}");
        }
    }
}
