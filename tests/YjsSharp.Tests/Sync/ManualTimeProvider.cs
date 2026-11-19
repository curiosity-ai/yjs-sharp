using System;
using System.Collections.Generic;
using System.Threading;

namespace YjsSharp.Tests.Sync;

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when a test calls <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new List<ManualTimer>();
    private          DateTimeOffset    _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public int ActiveTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Moves the clock forward, firing every timer that comes due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next = null;
            lock (_timers)
            {
                foreach (var t in _timers)
                {
                    if (t.Due <= target && (next == null || t.Due < next.Due))
                    {
                        next = t;
                    }
                }
            }
            if (next == null)
            {
                break;
            }
            _now = next.Due;
            next.Fire();
        }
        _now = target;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback      _callback;
        private readonly object?            _state;
        private          TimeSpan           _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner    = owner;
            _callback = callback;
            _state    = state;
        }

        public DateTimeOffset Due { get; private set; }

        public void Fire()
        {
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                Due += _period;
            }
            else
            {
                Remove();
            }
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Remove();
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Due = _owner._now + dueTime;
                lock (_owner._timers)
                {
                    _owner._timers.Add(this);
                }
            }
            return true;
        }

        public void Dispose() => Remove();

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Remove();
            return default;
        }

        private void Remove()
        {
            lock (_owner._timers)
            {
                _owner._timers.Remove(this);
            }
        }
    }
}
