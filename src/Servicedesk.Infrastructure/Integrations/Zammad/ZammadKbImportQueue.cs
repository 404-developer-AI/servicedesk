using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Hand-off channel between the API endpoint that starts a KB-import run
/// and the background worker that processes it. Same shape as
/// <see cref="IZammadDryRunQueue"/> — kept separate because KB-imports
/// and ticket dry-runs are different workers with different lifecycles.
public interface IZammadKbImportQueue
{
    bool TryEnqueue(Guid runId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

public sealed class ZammadKbImportQueue : IZammadKbImportQueue
{
    private readonly Channel<Guid> _channel;

    public ZammadKbImportQueue()
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(16)
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
