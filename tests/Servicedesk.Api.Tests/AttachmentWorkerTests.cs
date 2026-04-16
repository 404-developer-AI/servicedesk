using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class AttachmentWorkerTests
{
    private static readonly Guid AttachmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Happy_path_writes_blob_marks_ready_and_completes_job()
    {
        var jobs = new StubJobs(new AttachmentJobClaim(
            JobId: 10,
            Kind: "Ingest",
            PayloadJson: MakePayload(),
            AttemptCount: 1));
        var attachments = new StubAttachments();
        attachments.Rows[AttachmentId] = MakeAttachmentRow();
        var graph = new StubGraph(bytes: "hello world");
        var blobs = new StubBlobs();

        var ran = await AttachmentWorker.ProcessOneAsync(
            jobs, attachments, graph, blobs,
            maxAttempts: 7, retryBaseSeconds: 5,
            NullLogger<AttachmentWorkerTests>.Instance, default);

        Assert.True(ran.Ran);
        Assert.Contains(10L, jobs.Completed);
        Assert.Empty(jobs.Retried);
        Assert.Empty(jobs.DeadLettered);

        var stored = blobs.Writes.Single();
        Assert.Equal(stored.ContentHash, attachments.ReadyMarks[AttachmentId].ContentHash);
        Assert.Equal(stored.SizeBytes, attachments.ReadyMarks[AttachmentId].SizeBytes);
    }

    [Fact]
    public async Task Transient_failure_before_max_attempts_schedules_retry_without_dead_letter()
    {
        var jobs = new StubJobs(new AttachmentJobClaim(10, "Ingest", MakePayload(), AttemptCount: 3));
        var attachments = new StubAttachments();
        attachments.Rows[AttachmentId] = MakeAttachmentRow();
        var graph = new StubGraph(throwOnFetch: true);
        var blobs = new StubBlobs();

        var ran = await AttachmentWorker.ProcessOneAsync(
            jobs, attachments, graph, blobs,
            maxAttempts: 7, retryBaseSeconds: 1,
            NullLogger<AttachmentWorkerTests>.Instance, default);

        Assert.True(ran.Ran);
        Assert.Empty(jobs.Completed);
        var retry = jobs.Retried.Single();
        Assert.Equal(10, retry.JobId);
        Assert.True(retry.NextAttemptUtc > DateTime.UtcNow);
        Assert.Empty(jobs.DeadLettered);
        Assert.DoesNotContain(AttachmentId, attachments.Failed);
    }

    [Fact]
    public async Task Failure_at_max_attempts_dead_letters_job_and_marks_attachment_failed()
    {
        var jobs = new StubJobs(new AttachmentJobClaim(10, "Ingest", MakePayload(), AttemptCount: 7));
        var attachments = new StubAttachments();
        attachments.Rows[AttachmentId] = MakeAttachmentRow();
        var graph = new StubGraph(throwOnFetch: true);
        var blobs = new StubBlobs();

        var ran = await AttachmentWorker.ProcessOneAsync(
            jobs, attachments, graph, blobs,
            maxAttempts: 7, retryBaseSeconds: 1,
            NullLogger<AttachmentWorkerTests>.Instance, default);

        Assert.True(ran.Ran);
        Assert.Empty(jobs.Retried);
        Assert.Contains(10, jobs.DeadLettered);
        Assert.Contains(AttachmentId, attachments.Failed);
    }

    [Fact]
    public async Task Empty_queue_returns_false_without_side_effects()
    {
        var jobs = new StubJobs(null);

        var ran = await AttachmentWorker.ProcessOneAsync(
            jobs, new StubAttachments(), new StubGraph(), new StubBlobs(),
            maxAttempts: 7, retryBaseSeconds: 5,
            NullLogger<AttachmentWorkerTests>.Instance, default);

        Assert.False(ran.Ran);
        Assert.Empty(jobs.Completed);
        Assert.Empty(jobs.Retried);
        Assert.Empty(jobs.DeadLettered);
    }

    private static string MakePayload() => JsonSerializer.Serialize(new
    {
        attachment_id = AttachmentId,
        mailbox = "inbox@test",
        graph_message_id = "gid-msg",
        graph_attachment_id = "gid-att",
    });

    private static AttachmentRow MakeAttachmentRow() => new(
        Id: AttachmentId,
        OwnerId: Guid.NewGuid(),
        OwnerKind: "Mail",
        ContentHash: null,
        SizeBytes: 0,
        MimeType: "image/jpeg",
        OriginalFilename: "photo.jpg",
        IsInline: true,
        ContentId: "img-001",
        ProcessingState: "Pending");

    private sealed class StubJobs : IAttachmentJobRepository
    {
        private readonly AttachmentJobClaim? _next;
        public List<long> Completed { get; } = new();
        public List<(long JobId, DateTime NextAttemptUtc, string Error)> Retried { get; } = new();
        public List<long> DeadLettered { get; } = new();
        public StubJobs(AttachmentJobClaim? next) => _next = next;

        public Task<AttachmentJobClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken ct) => Task.FromResult(_next);
        public Task CompleteAsync(long jobId, TimeSpan duration, CancellationToken ct) { Completed.Add(jobId); return Task.CompletedTask; }
        public Task ScheduleRetryAsync(long jobId, DateTime nextAttemptUtc, string error, TimeSpan duration, CancellationToken ct)
        { Retried.Add((jobId, nextAttemptUtc, error)); return Task.CompletedTask; }
        public Task DeadLetterAsync(long jobId, string error, TimeSpan duration, CancellationToken ct) { DeadLettered.Add(jobId); return Task.CompletedTask; }
        public Task<int> CountPendingOlderThanAsync(TimeSpan threshold, DateTime nowUtc, CancellationToken ct) => Task.FromResult(0);
        public Task<int> CountDeadLetteredAsync(CancellationToken ct) => Task.FromResult(DeadLettered.Count);
        public Task<int> RequeueDeadLetteredAsync(DateTime nowUtc, CancellationToken ct) => Task.FromResult(0);
        public Task<int> CancelDeadLetteredAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubAttachments : IAttachmentRepository
    {
        public Dictionary<Guid, AttachmentRow> Rows { get; } = new();
        public Dictionary<Guid, (string ContentHash, long SizeBytes, string MimeType)> ReadyMarks { get; } = new();
        public HashSet<Guid> Failed { get; } = new();

        public Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var r) ? r : null);
        public Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AttachmentRow>>(Rows.Values.Where(r => r.OwnerId == mailId).ToList());
        public Task<bool> MarkReadyAsync(Guid id, string contentHash, long sizeBytes, string mimeType, CancellationToken ct)
        { ReadyMarks[id] = (contentHash, sizeBytes, mimeType); return Task.FromResult(true); }
        public Task MarkFailedAsync(Guid id, CancellationToken ct) { Failed.Add(id); return Task.CompletedTask; }
    }

    private sealed class StubGraph : IGraphMailClient
    {
        private readonly byte[] _bytes;
        private readonly bool _throwOnFetch;
        public StubGraph(string bytes = "", bool throwOnFetch = false)
        {
            _bytes = Encoding.UTF8.GetBytes(bytes);
            _throwOnFetch = throwOnFetch;
        }
        public Task<Stream> FetchAttachmentBytesAsync(string mailbox, string graphMessageId, string graphAttachmentId, CancellationToken ct)
        {
            if (_throwOnFetch) throw new InvalidOperationException("boom");
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
        public Task<GraphDeltaPage> ListInboxDeltaAsync(string m, string f, string? d, int b, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphMailFolderInfo>>(Array.Empty<GraphMailFolderInfo>());
        public Task<TimeSpan> PingAsync(string mbx, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task<GraphFullMessage> FetchMessageAsync(string m, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> FetchRawMessageAsync(string m, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task MarkAsReadAsync(string m, string id, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(string m, string id, string f, CancellationToken ct) => Task.CompletedTask;
        public Task<string> EnsureFolderAsync(string m, string n, CancellationToken ct) => Task.FromResult("f");
    }

    private sealed class StubBlobs : IBlobStore
    {
        public List<BlobWriteResult> Writes { get; } = new();
        public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var hash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
            var result = new BlobWriteResult(hash, ms.Length);
            Writes.Add(result);
            return result;
        }
        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task<bool> ExistsAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
    }
}
