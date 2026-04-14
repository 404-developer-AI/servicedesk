using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// Pulls Graph attachment bytes for inbound mails whose rows were enqueued by
/// <see cref="Ingest.MailIngestService"/>. Each worker loop claims one job at
/// a time via <c>FOR UPDATE SKIP LOCKED</c>; multiple loops can run in
/// parallel (concurrency is setting-driven) without stealing each other's
/// work. On permanent failure the job dead-letters and the attachment row
/// flips to <c>Failed</c>; on transient failure we schedule an exponential-
/// backoff retry.
public sealed class AttachmentWorker : BackgroundService
{
    // Sweeper throttle: most finalization happens via the per-complete hook;
    // the sweeper is a safety-net for cases the hook doesn't cover (mails
    // without attachments, restart mid-flight, setting toggled at runtime).
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<AttachmentWorker> _logger;
    private DateTime _lastSweepUtc = DateTime.MinValue;

    public AttachmentWorker(IServiceProvider services, ILogger<AttachmentWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AttachmentWorker started.");
        // Stagger startup so we don't hammer the queue while the app is booting.
        await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);

        int concurrency = await ReadIntSettingAsync(SettingKeys.Jobs.AttachmentWorkerConcurrency, 2, stoppingToken);
        if (concurrency < 1) concurrency = 1;
        if (concurrency > 8) concurrency = 8; // upper bound: Graph throttling + disk IO

        var loops = Enumerable.Range(0, concurrency)
            .Select(i => Task.Run(() => RunLoopAsync(i, stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(int index, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan idle;
            try
            {
                var ran = await RunOneAsync(stoppingToken);
                idle = ran ? TimeSpan.Zero : await ReadPollIntervalAsync(stoppingToken);

                // Throttled sweep: when the queue is idle is the cheapest
                // time to look for finalize-eligible mails that the
                // per-complete hook missed (e.g. mails without attachments).
                // Only loop 0 runs the sweeper to avoid duplicate Graph calls.
                if (index == 0 && DateTime.UtcNow - _lastSweepUtc >= SweepInterval)
                {
                    _lastSweepUtc = DateTime.UtcNow;
                    await RunSweepAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AttachmentWorker#{Index}] loop crashed — will sleep and retry.", index);
                idle = TimeSpan.FromSeconds(15);
            }

            if (idle > TimeSpan.Zero)
                await SafeDelayAsync(idle, stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<IMailFinalizer>();
        try
        {
            await finalizer.SweepAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AttachmentWorker] finalizer sweep failed (will retry next tick).");
        }
    }

    /// Claim + process one job. Returns <c>true</c> when work was done so the
    /// loop polls again immediately; <c>false</c> when the queue was empty.
    /// On completion (success OR dead-letter) of a mail-attachment job, the
    /// finalizer is invoked for that mail to move it out of the Inbox once
    /// every attachment is Ready.
    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var jobs = sp.GetRequiredService<IAttachmentJobRepository>();
        var attachments = sp.GetRequiredService<IAttachmentRepository>();
        var graph = sp.GetRequiredService<IGraphMailClient>();
        var blobs = sp.GetRequiredService<IBlobStore>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var finalizer = sp.GetRequiredService<IMailFinalizer>();

        var maxAttempts = SafeRead(() => settings.GetAsync<int>(SettingKeys.Jobs.AttachmentMaxAttempts, ct), 7);
        var retryBase = SafeRead(() => settings.GetAsync<int>(SettingKeys.Jobs.AttachmentRetryBaseSeconds, ct), 5);

        var outcome = await ProcessOneAsync(jobs, attachments, graph, blobs, maxAttempts, retryBase, _logger, ct);
        if (outcome.Ran && outcome.TerminalForAttachmentId is Guid attachmentRowId)
        {
            // Attachment row → mail row. Only Mail-owned rows trigger the
            // finalizer; User/Ticket-owned rows (future use) are skipped.
            var row = await attachments.GetByIdAsync(attachmentRowId, ct);
            if (row is { OwnerKind: "Mail" })
            {
                try { await finalizer.TryFinalizeMailAsync(row.OwnerId, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[AttachmentWorker] finalizer hook failed for mail {MailId} — sweeper will retry.",
                        row.OwnerId);
                }
            }
        }
        return outcome.Ran;
    }

    /// Outcome of one worker tick. <see cref="Ran"/> indicates whether any
    /// job was claimed; <see cref="TerminalForAttachmentId"/> is set when the
    /// tick moved an attachment row into a terminal state (Ready or Failed)
    /// — the caller uses this to kick the finalizer for that mail.
    internal readonly record struct ProcessOutcome(bool Ran, Guid? TerminalForAttachmentId);

    /// Pure orchestration — no DI scope, no settings reads. Tests drive this
    /// directly with stubs; the loop above wraps it with fresh scopes.
    internal static async Task<ProcessOutcome> ProcessOneAsync(
        IAttachmentJobRepository jobs,
        IAttachmentRepository attachments,
        IGraphMailClient graph,
        IBlobStore blobs,
        int maxAttempts,
        int retryBaseSeconds,
        ILogger logger,
        CancellationToken ct)
    {
        var job = await jobs.ClaimNextAsync(DateTime.UtcNow, ct);
        if (job is null) return new ProcessOutcome(false, null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var payload = ParsePayload(job.PayloadJson);
            logger.LogInformation(
                "[AttachmentWorker] jobId={JobId} attachment={AttachmentId} claim mailbox={Mailbox} graphMessageId={GraphMessageId} attempt={Attempt}",
                job.JobId, payload.AttachmentId, payload.Mailbox, payload.GraphMessageId, job.AttemptCount + 1);
            var ingested = await IngestAsync(payload, graph, blobs, attachments, ct);
            sw.Stop();
            await jobs.CompleteAsync(job.JobId, sw.Elapsed, ct);
            logger.LogInformation(
                "[AttachmentWorker] jobId={JobId} attachment={AttachmentId} ready size={Size} hash={HashPrefix} in {Ms}ms",
                job.JobId, payload.AttachmentId, ingested.SizeBytes,
                ingested.ContentHash.Length >= 8 ? ingested.ContentHash[..8] : ingested.ContentHash,
                sw.ElapsedMilliseconds);
            return new ProcessOutcome(true, payload.AttachmentId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            var errorDetail = DescribeError(ex);
            var payloadForErr = TryParsePayload(job.PayloadJson);
            if (job.AttemptCount >= maxAttempts)
            {
                await jobs.DeadLetterAsync(job.JobId, Truncate(errorDetail, 2000), sw.Elapsed, ct);
                if (payloadForErr is not null)
                    await attachments.MarkFailedAsync(payloadForErr.AttachmentId, ct);
                logger.LogError(ex,
                    "[AttachmentWorker] jobId={JobId} dead-lettered after {Attempts} attempts. attachment={AttachmentId} graphMessageId={GraphMessageId} graphAttachmentId={GraphAttachmentId} detail={Detail}",
                    job.JobId, job.AttemptCount,
                    payloadForErr?.AttachmentId, payloadForErr?.GraphMessageId, payloadForErr?.GraphAttachmentId,
                    errorDetail);
                // Terminal Failed — surface so the finalizer can run (and
                // deliberately leave the mail in Inbox per chosen policy).
                return new ProcessOutcome(true, payloadForErr?.AttachmentId);
            }
            else
            {
                var delay = ComputeBackoff(job.AttemptCount, retryBaseSeconds);
                await jobs.ScheduleRetryAsync(job.JobId,
                    DateTime.UtcNow.Add(delay),
                    Truncate(errorDetail, 2000),
                    sw.Elapsed, ct);
                logger.LogWarning(ex,
                    "[AttachmentWorker] jobId={JobId} attempt {Attempt} failed — retry in {Delay}. attachment={AttachmentId} graphMessageId={GraphMessageId} graphAttachmentId={GraphAttachmentId} detail={Detail}",
                    job.JobId, job.AttemptCount, delay,
                    payloadForErr?.AttachmentId, payloadForErr?.GraphMessageId, payloadForErr?.GraphAttachmentId,
                    errorDetail);
                // Transient — job goes back to Pending, not terminal yet.
                return new ProcessOutcome(true, null);
            }
        }
    }

    private static int SafeRead(Func<Task<int>> read, int fallback)
    {
        try
        {
            var v = read().GetAwaiter().GetResult();
            return v > 0 ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task<BlobWriteResult> IngestAsync(
        JobPayload p, IGraphMailClient graph, IBlobStore blobs,
        IAttachmentRepository attachments, CancellationToken ct)
    {
        await using var stream = await graph.FetchAttachmentBytesAsync(
            p.Mailbox, p.GraphMessageId, p.GraphAttachmentId, ct);
        var result = await blobs.WriteAsync(stream, ct);

        var row = await attachments.GetByIdAsync(p.AttachmentId, ct)
            ?? throw new InvalidOperationException(
                $"Attachment row {p.AttachmentId} disappeared between enqueue and ingest.");

        await attachments.MarkReadyAsync(
            p.AttachmentId,
            result.ContentHash,
            result.SizeBytes,
            row.MimeType,
            ct);

        return result;
    }

    private static TimeSpan ComputeBackoff(int attemptCount, int baseSeconds)
    {
        // Exponential + ±20% jitter, capped at 6 hours. attemptCount is the
        // attempt we just finished (1-based), so the next delay uses (n-1).
        var exp = Math.Min(attemptCount - 1, 12); // cap 2^12 = 4096s to avoid overflow
        var baseDelay = TimeSpan.FromSeconds(Math.Max(baseSeconds, 1) * Math.Pow(2, exp));
        if (baseDelay > TimeSpan.FromHours(6)) baseDelay = TimeSpan.FromHours(6);
        var jitterMs = Random.Shared.Next(-(int)(baseDelay.TotalMilliseconds * 0.2),
                                           (int)(baseDelay.TotalMilliseconds * 0.2) + 1);
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }

    private async Task<TimeSpan> ReadPollIntervalAsync(CancellationToken ct)
    {
        var seconds = await ReadIntSettingAsync(SettingKeys.Jobs.AttachmentWorkerPollSeconds, 5, ct);
        return TimeSpan.FromSeconds(Math.Max(seconds, 1));
    }

    private async Task<int> ReadIntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        try
        {
            var v = await settings.GetAsync<int>(key, ct);
            return v > 0 ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static JobPayload ParsePayload(string json)
        => TryParsePayload(json) ?? throw new InvalidOperationException("Attachment job payload is invalid JSON or missing fields.");

    private static JobPayload? TryParsePayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var attachmentId = root.GetProperty("attachment_id").GetGuid();
            var mailbox = root.GetProperty("mailbox").GetString() ?? "";
            var msgId = root.GetProperty("graph_message_id").GetString() ?? "";
            var attId = root.GetProperty("graph_attachment_id").GetString() ?? "";
            if (mailbox.Length == 0 || msgId.Length == 0 || attId.Length == 0) return null;
            return new JobPayload(attachmentId, mailbox, msgId, attId);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    /// Graph SDK exceptions carry the useful bits (error code, inner message,
    /// request-id, details) on sub-properties. The top-level Message is often
    /// just "The specified object was not found in the store." — fine for a
    /// retry decision but useless for root-causing. This flattens everything
    /// we can reach into a single line.
    private static string DescribeError(Exception ex)
    {
        if (ex is Microsoft.Graph.Models.ODataErrors.ODataError ode)
        {
            var parts = new List<string> { $"ODataError status={ode.ResponseStatusCode}" };
            var main = ode.Error;
            if (main is not null)
            {
                if (!string.IsNullOrWhiteSpace(main.Code)) parts.Add($"code={main.Code}");
                if (!string.IsNullOrWhiteSpace(main.Message)) parts.Add($"msg={main.Message}");
                if (!string.IsNullOrWhiteSpace(main.Target)) parts.Add($"target={main.Target}");
                if (main.Details is { Count: > 0 })
                {
                    foreach (var d in main.Details)
                        parts.Add($"detail[{d.Code}:{d.Target}]={d.Message}");
                }
                if (main.AdditionalData is { Count: > 0 } extra)
                {
                    foreach (var kv in extra)
                        parts.Add($"extra.{kv.Key}={kv.Value}");
                }
            }
            return string.Join(" | ", parts);
        }
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    private sealed record JobPayload(Guid AttachmentId, string Mailbox, string GraphMessageId, string GraphAttachmentId);
}
