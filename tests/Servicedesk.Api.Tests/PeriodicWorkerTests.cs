using Servicedesk.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The shared worker loop (ROADMAP §2 C2): a throwing iteration never kills
/// the loop, a disabled schedule skips the work but keeps polling, a wake
/// signal cuts the wait short, and stopping the host ends it cleanly.
public sealed class PeriodicWorkerTests
{
    [Fact]
    public async Task A_failing_iteration_is_logged_and_the_loop_keeps_running()
    {
        var worker = new ProbeWorker(TimeSpan.FromMilliseconds(10)) { ThrowOnRun = true };

        await RunUntilAsync(worker, () => worker.Runs >= 3);
    }

    [Fact]
    public async Task A_disabled_schedule_skips_the_work_but_keeps_reading_the_schedule()
    {
        var worker = new ProbeWorker(TimeSpan.FromMilliseconds(10)) { Enabled = false };

        await RunUntilAsync(worker, () => worker.ScheduleReads >= 3);

        Assert.Equal(0, worker.Runs);
    }

    [Fact]
    public async Task A_wake_signal_runs_the_next_iteration_without_waiting_the_interval()
    {
        var signal = new ProbeSignal();
        var worker = new ProbeWorker(TimeSpan.FromHours(1), signal);

        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => worker.Runs >= 1);
        signal.RequestImmediateRun();
        await WaitForAsync(() => worker.Runs >= 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, worker.Runs);
    }

    [Fact]
    public async Task A_non_positive_interval_falls_back_instead_of_spinning()
    {
        // Schedule says zero; the fallback is an hour. Without the guard the
        // loop runs back to back.
        var worker = new ProbeWorker(TimeSpan.FromHours(1)) { ScheduledInterval = TimeSpan.Zero };
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => worker.Runs >= 1);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, worker.Runs);
    }

    [Fact]
    public async Task Stopping_ends_the_loop_during_the_wait()
    {
        var worker = new ProbeWorker(TimeSpan.FromHours(1));
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => worker.Runs >= 1);

        var stop = worker.StopAsync(CancellationToken.None);

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(1, worker.Runs);
    }

    // ---- plumbing -----------------------------------------------------------

    private static async Task RunUntilAsync(ProbeWorker worker, Func<bool> done)
    {
        await worker.StartAsync(CancellationToken.None);
        try { await WaitForAsync(done); }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private static Task WaitForAsync(Func<bool> done) => Eventually.TrueAsync(done, "the worker condition");

    private sealed class ProbeSignal : WorkerWakeSignal;

    private sealed class ProbeWorker : PeriodicWorker
    {
        private readonly TimeSpan _interval;

        public ProbeWorker(TimeSpan interval, IWorkerWakeSignal? signal = null)
            : base(new ServiceCollection().BuildServiceProvider(), NullLogger.Instance)
        {
            _interval = interval;
            _signal = signal;
        }

        private readonly IWorkerWakeSignal? _signal;
        public bool Enabled { get; init; } = true;
        public bool ThrowOnRun { get; init; }
        /// What the schedule reports; defaults to the fallback interval.
        public TimeSpan? ScheduledInterval { get; init; }
        public int Runs;
        public int ScheduleReads;

        protected override string WorkerName => "probe";
        protected override TimeSpan FallbackInterval => _interval;
        protected override IWorkerWakeSignal? WakeSignal => _signal;

        protected override Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
        {
            Interlocked.Increment(ref ScheduleReads);
            return Task.FromResult(new WorkerSchedule(ScheduledInterval ?? _interval, Enabled));
        }

        protected override Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return ThrowOnRun ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
        }
    }
}
