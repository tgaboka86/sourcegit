using SourceGit.Models;

await CoalescesRequests();
await CategoriesRunIndependently();
await RecoversAfterFailure();
await CloseDiscardsPendingAndCancelsActive();
await CloseDoesNotWaitForProcessTermination();
RecoveryWaitsForQuietAndHasABound();
await WatcherRecoversOnceAndHonorsOperationLock();
Console.WriteLine("Refresh coordinator and watcher recovery checks passed.");

static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static async Task CoalescesRequests()
{
    var started = Signal();
    var release = Signal();
    var runs = 0;
    var active = 0;
    var applied = 0;
    var errors = new List<Exception>();
    using var coordinator = new RefreshCoordinator(errors.Add);
    async Task Refresh(RefreshCoordinator.Context context)
    {
        Check(Interlocked.Increment(ref active) == 1, "Same category ran concurrently.");
        if (Interlocked.Increment(ref runs) == 1)
        {
            started.SetResult();
            await release.Task;
            Check(!context.CancellationToken.IsCancellationRequested, "A dirty request cancelled the active query.");
        }

        if (context.IsCurrent)
            Interlocked.Increment(ref applied);
        Interlocked.Decrement(ref active);
    }

    coordinator.Request(RefreshKind.Commits, Refresh);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Parallel.For(0, 1000, _ => coordinator.Request(RefreshKind.Commits, Refresh));
    release.SetResult();
    await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(runs == 2 && applied == 1, $"Expected one follow-up and one current publication, got {runs}/{applied}.");
    Check(errors.Count == 0, "Coordinator swallowed a check failure.");

    coordinator.Request(RefreshKind.Commits, Refresh);
    await coordinator.WhenIdleAsync();
    Check(runs == 3 && applied == 2, "A request after completion was lost.");
}

static async Task CategoriesRunIndependently()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    var blocked = Signal();
    var release = Signal();
    var independent = Signal();
    coordinator.Request(RefreshKind.Commits, async _ =>
    {
        blocked.SetResult();
        await release.Task;
    });
    await blocked.Task;
    coordinator.Request(RefreshKind.WorkingCopy, _ =>
    {
        independent.SetResult();
        return Task.CompletedTask;
    });
    await independent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    release.SetResult();
    await coordinator.WhenIdleAsync();
}

static async Task RecoversAfterFailure()
{
    var errors = 0;
    using var coordinator = new RefreshCoordinator(_ => Interlocked.Increment(ref errors));
    coordinator.Request(RefreshKind.Tags, _ => throw new IOException("Expected failure"));
    await coordinator.WhenIdleAsync();
    Check(errors == 1, "Failed refresh was not reported.");
    var ran = false;
    coordinator.Request(RefreshKind.Tags, _ => { ran = true; return Task.CompletedTask; });
    await coordinator.WhenIdleAsync();
    Check(ran, "Failure left the category stuck.");

    var started = Signal();
    var release = Signal();
    coordinator.Request(RefreshKind.Tags, async _ =>
    {
        started.SetResult();
        await release.Task;
        throw new IOException("Failure with a pending refresh");
    });
    await started.Task;
    ran = false;
    coordinator.Request(RefreshKind.Tags, _ => { ran = true; return Task.CompletedTask; });
    release.SetResult();
    await coordinator.WhenIdleAsync();
    Check(ran, "Failure dropped a pending refresh.");
}

static async Task CloseDoesNotWaitForProcessTermination()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    using var releaseTermination = new ManualResetEventSlim();
    var started = Signal();
    var terminating = Signal();
    coordinator.Request(RefreshKind.WorkingCopy, async context =>
    {
        using var registration = context.CancellationToken.Register(() =>
        {
            terminating.SetResult();
            releaseTermination.Wait();
        });
        started.SetResult();
        await Task.Delay(Timeout.Infinite, context.CancellationToken);
    });
    await started.Task;
    var closing = Task.Run(coordinator.Dispose);
    try
    {
        await terminating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closing.WaitAsync(TimeSpan.FromSeconds(1));
    }
    finally
    {
        releaseTermination.Set();
    }
    await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task CloseDiscardsPendingAndCancelsActive()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    var started = Signal();
    var cancelled = Signal();
    var runs = 0;
    coordinator.Request(RefreshKind.WorkingCopy, async context =>
    {
        Interlocked.Increment(ref runs);
        started.SetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Check(!context.IsCurrent, "Closed repository can still publish results.");
            cancelled.SetResult();
        }
    });
    await started.Task;
    coordinator.Request(RefreshKind.WorkingCopy, _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
    coordinator.Dispose();
    coordinator.Dispose();
    coordinator.Request(RefreshKind.Tags, _ => throw new Exception("Request after close ran."));
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coordinator.WhenIdleAsync();
    Check(runs == 1, "Close started a pending refresh.");
}

static void RecoveryWaitsForQuietAndHasABound()
{
    var recovery = new WatcherRecovery();
    recovery.Schedule(100);
    recovery.Schedule(200);
    recovery.RecordActivity(900);
    Check(!recovery.TryTake(1899), "Recovery did not wait for quiet.");
    Check(recovery.TryTake(1900), "Recovery missed the end of the quiet period.");
    Check(!recovery.TryTake(10000), "One overflow burst caused multiple recoveries.");

    recovery.Schedule(11000);
    for (var now = 11100; now < 16000; now += 100)
    {
        recovery.Schedule(now);
        recovery.RecordActivity(now);
        Check(!recovery.TryTake(now), "Continuous events triggered recovery too early.");
    }
    recovery.RecordActivity(16000);
    Check(recovery.TryTake(16000), "Repeated errors postponed recovery past the maximum wait.");

    recovery.Schedule(16001);
    Check(!recovery.TryTake(16002), "Next burst inherited the previous deadline.");
    Check(recovery.TryTake(17001), "A new overflow after recovery was dropped.");
}

static async Task WatcherRecoversOnceAndHonorsOperationLock()
{
    var path = Path.Combine(Path.GetTempPath(), $"sourcegit-watcher-check-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(path, ".git"));
    try
    {
        var repository = new RecordingRepository();
        using var watcher = new Watcher(repository, path, Path.Combine(path, ".git"));
        using var operation = watcher.Lock();
        // Inject native buffer errors; the subsequent dispatch still uses the real timer.
        var error = typeof(Watcher).GetMethod("OnWatcherError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 20; i++)
            error.Invoke(watcher, [null, new ErrorEventArgs(new InternalBufferOverflowException())]);
        await Task.Delay(1200);
        Check(repository.Total == 0, "Watcher refreshed during an application operation.");
        operation.Dispose();
        operation.Dispose();
        await repository.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Check(repository.Total == 7, $"Expected one complete recovery, got {repository.Total} calls.");
        watcher.Dispose();
        error.Invoke(watcher, [null, new ErrorEventArgs(new InternalBufferOverflowException())]);
        await Task.Delay(1200);
        Check(repository.Total == 7, "Disposed watcher still dispatched refreshes.");
    }
    finally
    {
        Directory.Delete(path, true);
    }
}

sealed class RecordingRepository : IRepository
{
    public int Total => Volatile.Read(ref _total);
    public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool MayHaveSubmodules() => false;
    public void RefreshBranches() => Record();
    public void RefreshWorktrees() => Record();
    public void RefreshTags() => Record();
    public void RefreshCommits() => Record();
    public void RefreshSubmodules() => Record();
    public void RefreshWorkingCopyChanges() => Record();
    public void RefreshStashes() => Record();
    private void Record()
    {
        if (Interlocked.Increment(ref _total) == 7)
            Recovered.TrySetResult();
    }
    private int _total;
}
