namespace SourceGit.Models
{
    /// <summary>
    ///     Combines lost filesystem notifications into one full refresh after a
    ///     quiet period. A continuous stream of events cannot postpone it forever.
    ///     The watcher supplies monotonic milliseconds from Environment.TickCount64.
    /// </summary>
    internal sealed class WatcherRecovery
    {
        internal const long QuietPeriod = 1000;
        internal const long MaximumWait = 5000;

        public void Schedule(long now)
        {
            lock (_syncRoot)
            {
                if (!_pending)
                    _firstError = now;
                _pending = true;
                _lastActivity = now;
            }
        }

        public void RecordActivity(long now)
        {
            lock (_syncRoot)
            {
                if (_pending)
                    _lastActivity = now;
            }
        }

        public bool IsPending
        {
            get
            {
                lock (_syncRoot)
                    return _pending;
            }
        }

        public bool TryTake(long now)
        {
            lock (_syncRoot)
            {
                if (!_pending || (now - _lastActivity < QuietPeriod && now - _firstError < MaximumWait))
                    return false;

                _pending = false;
                return true;
            }
        }

        private readonly object _syncRoot = new();
        private bool _pending;
        private long _firstError;
        private long _lastActivity;
    }
}
