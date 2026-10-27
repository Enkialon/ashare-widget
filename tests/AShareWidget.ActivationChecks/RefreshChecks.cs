using AShareWidget;

static class RefreshChecks
{
    public static async Task RunAsync()
    {
        var clock = new TestClock();
        int queries = 0;
        int submissions = 0;
        TaskCompletionSource? pending = null;
        bool fail = false;
        var scheduler = new RefreshScheduler(() => TimeSpan.FromSeconds(15), async () =>
        {
            queries++;
            if (pending is not null) await pending.Task;
            if (fail) throw new Exception("simulated network failure");
            submissions++;
        }, clock);

        await scheduler.RequestAsync(initialCreation: true);
        Check(queries == 1 && submissions == 1, "first creation fetches content");
        await scheduler.RequestAsync(initialCreation: true);
        Check(queries == 1, "additional new card reuses fresh shared quotes");
        scheduler.SetActive(true);
        for (int i = 0; i < 20; i++)
        {
            scheduler.SetActive(false);
            scheduler.SetActive(true);
            await scheduler.RequestAsync();
        }
        Check(queries == 1 && submissions == 1, "fresh reopen/monitor switches do not fetch or submit");
        Check(clock.Timer.Due == TimeSpan.FromSeconds(15), "no extra one-second refresh");

        clock.Advance(14);
        clock.Timer.Fire();
        Check(queries == 1 && clock.Timer.Due == TimeSpan.FromSeconds(1), "early timer waits only remaining freshness");
        clock.Advance(1);
        clock.Timer.Fire();
        Check(queries == 2 && submissions == 2, "visible timer refreshes at expiry");

        scheduler.SetActive(false);
        Check(clock.Timer.Due == Timeout.InfiniteTimeSpan, "closing disables timer");
        clock.Advance(60);
        await scheduler.RequestAsync();
        Check(queries == 2, "closed board does not query even after expiry");

        pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.SetActive(true);
        var refresh = scheduler.RequestAsync();
        Check(queries == 3 && submissions == 2, "expired reopen keeps old content during HTTP");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => scheduler.RequestAsync()));
        Check(queries == 3, "concurrent activations share in-flight query");
        scheduler.SetActive(false);
        pending.SetResult();
        await refresh;
        pending = null;
        Check(submissions == 3 && clock.Timer.Due == Timeout.InfiniteTimeSpan,
            "finishing in-flight query after close does not restart timer");

        clock.Advance(15);
        scheduler.SetActive(true);
        fail = true;
        await scheduler.RequestAsync();
        await scheduler.RequestAsync();
        Check(queries == 4 && submissions == 3, "failure keeps content and throttles retries");
        clock.Advance(15);
        fail = false;
        await scheduler.RequestAsync();
        Check(queries == 5 && submissions == 4, "failed query retries after interval");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    sealed class TestClock : TimeProvider
    {
        long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
        public TestTimer Timer { get; private set; } = null!;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new TestTimer(callback, state, dueTime);
    }

    sealed class TestTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return true; }
        public void Fire() { Due = Timeout.InfiniteTimeSpan; callback(state); }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
