using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    /// <summary>
    /// Tracks whether texts can go out (Call Centre connected and the phone known to it) and
    /// turns that into outages. An outage starts once the link has been down for OutageStartAfter
    /// and ends once it has stayed up for OutageEndAfter, so a flapping link is one outage.
    /// dental_practice_admin keys its alert and all-clear emails on the outage id and never
    /// re-derives these rules. State is in memory: a restarted bridge starts fresh.
    /// </summary>
    public class PhoneLinkMonitor : IDisposable
    {
        public static readonly TimeSpan OutageStartAfter = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan OutageEndAfter = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan EvaluateEvery = TimeSpan.FromSeconds(15);

        private readonly TimeProvider _clock;
        private readonly bool _canShowPopup;
        private readonly ITimer _evaluateTimer;
        private readonly object _lock = new();

        private bool _up;
        private DateTimeOffset _since;
        private string _detail = "Starting: not yet connected to Call Centre";
        private PhoneLinkOutage? _outage;

        public event Action<PhoneLinkOutage, string> OutageStarted = delegate { };
        public event Action<PhoneLinkOutage> OutageEnded = delegate { };

        public PhoneLinkMonitor(TimeProvider clock, bool canShowPopup)
        {
            _clock = clock;
            _canShowPopup = canShowPopup;
            _since = clock.GetUtcNow();
            // State changes arrive as events, but crossing a threshold is the passing of time.
            _evaluateTimer = clock.CreateTimer(_ => Evaluate(), null, EvaluateEvery, EvaluateEvery);
        }

        public void Report(bool up, string detail)
        {
            lock (_lock)
            {
                if (up != _up)
                {
                    _up = up;
                    _since = _clock.GetUtcNow();
                }
                else
                {
                    // Same side of the link; only the detail may have changed
                }
                _detail = detail;
            }
            Evaluate();
        }

        public void Evaluate()
        {
            PhoneLinkOutage? started = null;
            PhoneLinkOutage? ended = null;
            string detail;

            lock (_lock)
            {
                var heldFor = _clock.GetUtcNow() - _since;
                if (!_up && _outage == null && heldFor >= OutageStartAfter)
                {
                    _outage = new PhoneLinkOutage(Guid.NewGuid(), _since);
                    started = _outage;
                }
                else if (_up && _outage != null && heldFor >= OutageEndAfter)
                {
                    ended = _outage;
                    _outage = null;
                }
                else
                {
                    // No threshold crossed
                }
                detail = _detail;
            }

            // Raised outside the lock: handlers log and show a message box.
            if (started != null) OutageStarted(started, detail);
            if (ended != null) OutageEnded(ended);
        }

        public PhoneStatusResponse GetStatus()
        {
            Evaluate();
            lock (_lock)
            {
                return new PhoneStatusResponse(_up, _since, _detail, _outage, _canShowPopup);
            }
        }

        public void Dispose() => _evaluateTimer.Dispose();
    }
}
