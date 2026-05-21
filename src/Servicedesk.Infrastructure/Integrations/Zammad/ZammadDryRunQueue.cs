using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// <see cref="Channel{T}"/>-backed implementation of
/// <see cref="IZammadDryRunQueue"/>. Bounded at 32 outstanding runs —
/// the use case is admin-driven and a single run already covers
/// thousands of tickets; piling up dozens of runs is almost always a
/// mistake. Single-reader (the worker) / multi-writer (API endpoints).
public sealed class ZammadDryRunQueue : IZammadDryRunQueue
{
    private readonly Channel<Guid> _channel;

    public ZammadDryRunQueue()
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryEnqueue(Guid runId) => _channel.Writer.TryWrite(runId);

    public async IAsyncEnumerable<Guid> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out var id))
            {
                yield return id;
            }
        }
    }
}
