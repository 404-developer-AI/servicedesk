namespace Servicedesk.Infrastructure.Storage;

public sealed class BlobStoreHealth : IBlobStoreHealth
{
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private string? _lastError;
    private System.DateTime? _lastErrorUtc;
    private string? _lastOperation;
    private System.DateTime? _lastSuccessUtc;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _lastError = null;
            _lastErrorUtc = null;
            _lastOperation = null;
            _lastSuccessUtc = System.DateTime.UtcNow;
        }
    }

    public void RecordFailure(string operation, System.Exception exception)
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            _lastError = exception.Message;
            _lastErrorUtc = System.DateTime.UtcNow;
            _lastOperation = operation;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _lastError = null;
            _lastErrorUtc = null;
            _lastOperation = null;
        }
    }

    public BlobStoreHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BlobStoreHealthSnapshot(
                _consecutiveFailures, _lastError, _lastErrorUtc, _lastOperation, _lastSuccessUtc);
        }
    }
}
