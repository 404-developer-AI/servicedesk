using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Infrastructure.Hosting;

/// How often a worker runs and whether this iteration does work.
public readonly record struct WorkerSchedule(TimeSpan Interval, bool Enabled = true);

/// A "run now" request for a worker (an admin's *Sync now*). Concurrent
/// presses coalesce into one run; per-process state, so register singleton.
public interface IWorkerWakeSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

/// Base for the per-worker signal types (each worker keeps its own interface
/// so DI and the endpoints stay explicit).
public abstract class WorkerWakeSignal : IWorkerWakeSignal
{
    private int _requested;

    public void RequestImmediateRun() => Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => Interlocked.Exchange(ref _requested, 0) == 1;
}

/// The one loop for interval background workers (ROADMAP §2 C2): an initial
/// stagger, then per iteration a fresh DI scope, the schedule (usually two
/// settings: interval + enabled), the work inside a <see cref="PerfWorkerRun"/>
/// (a throw marks the run failed), error logging that never kills the loop,
/// and a wait that a <see cref="WakeSignal"/> can cut short. Iterations run
/// one after another, so a run can never overlap the previous one.
///
/// Settings are read every iteration, so a changed interval or switch takes
/// effect on the next run without a restart.
public abstract class PeriodicWorker : BackgroundService
{
    private static readonly TimeSpan WakePollSlice = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;

    protected PeriodicWorker(IServiceProvider services, ILogger logger)
    {
        _services = services;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// The <see cref="PerfWorkerRun"/> key and log name, e.g. "veeam-sync".
    protected abstract string WorkerName { get; }

    /// Wait before the first iteration — staggers startup work across workers.
    protected virtual TimeSpan StartDelay => TimeSpan.Zero;

    /// Interval used when reading the schedule fails.
    protected abstract TimeSpan FallbackInterval { get; }

    /// Optional "run now" signal; checked every two seconds while waiting.
    protected virtual IWorkerWakeSignal? WakeSignal => null;

    /// Reads this iteration's schedule. The default runs every
    /// <see cref="FallbackInterval"/>, always enabled.
    protected virtual Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct) =>
        Task.FromResult(new WorkerSchedule(FallbackInterval));

    /// One iteration of work. <paramref name="run"/> takes item counts and
    /// handled failures; an exception thrown out of here is logged and marks
    /// the run failed.
    protected abstract Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(StartDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = FallbackInterval;
            try
            {
                using var scope = _services.CreateScope();
                var schedule = await GetScheduleAsync(scope.ServiceProvider, stoppingToken);
                interval = schedule.Interval;
                if (schedule.Enabled)
                {
                    using var run = PerfWorkerRun.Start(WorkerName);
                    try
                    {
                        await RunAsync(scope.ServiceProvider, run, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        run.Fail(ex);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Worker} iteration failed.", WorkerName);
            }

            if (!await WaitForNextRunAsync(interval, stoppingToken)) return;
        }
    }

    /// False when the host is stopping.
    private async Task<bool> WaitForNextRunAsync(TimeSpan interval, CancellationToken ct)
    {
        if (WakeSignal is not { } signal) return await DelayAsync(interval, ct);

        var deadline = DateTime.UtcNow + interval;
        while (!ct.IsCancellationRequested)
        {
            if (signal.ConsumeRequest()) return true;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return true;
            if (!await DelayAsync(remaining < WakePollSlice ? remaining : WakePollSlice, ct)) return false;
        }
        return false;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return !ct.IsCancellationRequested;
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
