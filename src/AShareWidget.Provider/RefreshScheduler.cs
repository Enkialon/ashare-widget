namespace AShareWidget;

/// <summary>
/// Shares one request across activations and timers. Freshness is measured from
/// request completion, including failures, so reopening cannot cause retry storms.
/// </summary>
sealed class RefreshScheduler
{
    readonly object _gate = new();
    readonly TimeProvider _clock;
    readonly Func<TimeSpan> _interval;
    readonly Func<Task> _refresh;
    readonly ITimer _timer;
    bool _active;
    bool _busy;
    long? _lastCompleted;

    public RefreshScheduler(Func<TimeSpan> interval, Func<Task> refresh, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _interval = interval;
        _refresh = refresh;
        _timer = _clock.CreateTimer(_ => _ = RequestAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            _active = active;
            ScheduleNext();
        }
    }

    public async Task RequestAsync(bool initialCreation = false)
    {
        lock (_gate)
        {
            if (_busy || (!_active && !initialCreation))
                return;
            if (_lastCompleted.HasValue && Remaining() > TimeSpan.Zero)
            {
                ScheduleNext();
                return;
            }
            _busy = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        try
        {
            await _refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProviderLog.Error("scheduled refresh", ex);
        }
        finally
        {
            lock (_gate)
            {
                _lastCompleted = _clock.GetTimestamp();
                _busy = false;
                ScheduleNext();
            }
        }
    }

    TimeSpan Remaining() => _lastCompleted.HasValue
        ? _interval() - _clock.GetElapsedTime(_lastCompleted.Value)
        : TimeSpan.Zero;

    void ScheduleNext()
    {
        var due = _active && !_busy ? Remaining() : Timeout.InfiniteTimeSpan;
        if (_active && !_busy && due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }
}
