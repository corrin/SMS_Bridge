using System.Collections.Concurrent;
using SMS_Bridge.Models;

namespace SMS_Bridge.Services
{
    // Tracks work in progress so phone-status can report a bridge whose threads are stuck,
    // which a quiet bridge (no webhooks overnight, an empty queue) never looks like.
    public class BridgeHealth
    {
        public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<long, (string What, DateTime StartedAt)> _inFlight = new();
        private readonly Func<DateTime> _utcNow;
        private long _nextId;

        public BridgeHealth(Func<DateTime>? utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public IDisposable Track(string what)
        {
            var id = Interlocked.Increment(ref _nextId);
            _inFlight[id] = (what, _utcNow());
            return new Operation(() => _inFlight.TryRemove(id, out _));
        }

        // The oldest operation running longer than limit, or null when nothing is stuck.
        public string? Stuck(TimeSpan limit)
        {
            var now = _utcNow();
            return _inFlight.Values
                .Where(op => now - op.StartedAt > limit)
                .OrderBy(op => op.StartedAt)
                .Select(op => $"{op.What} stuck {(int)(now - op.StartedAt).TotalSeconds}s")
                .FirstOrDefault();
        }

        // Texts can't go out while the bridge is stuck, however healthy the phone link looks.
        public PhoneStatusResponse WithPhone(PhoneStatusResponse phone)
        {
            var stuck = Stuck(StuckAfter);
            return stuck == null
                ? phone
                : new PhoneStatusResponse(Up: false, Detail: $"{phone.Detail}, bridge: {stuck}");
        }

        private sealed class Operation(Action onDispose) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            }
        }
    }
}
