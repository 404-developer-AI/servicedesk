using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Api.Performance;

/// Times every hub method invocation and counts connects/disconnects per
/// hub. Registered globally (AddSignalR → AddFilter), so new hubs are
/// covered without wiring. Failures inside the filter never reach the hub.
public sealed class PerfHubFilter : IHubFilter
{
    private readonly PerfRecorder _recorder;
    private readonly IPerfSettings _settings;

    public PerfHubFilter(PerfRecorder recorder, IPerfSettings settings)
    {
        _recorder = recorder;
        _settings = settings;
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!_settings.IsEnabled(PerfCollector.SignalR)) return await next(invocationContext);

        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            return await next(invocationContext);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Record("hub", invocationContext.Hub.GetType().Name, invocationContext.HubMethodName,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, failed);
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context);
        if (_settings.IsEnabled(PerfCollector.SignalR)) Record("hub-connection", context.Hub.GetType().Name, "connect", 0, false);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        await next(context, exception);
        if (_settings.IsEnabled(PerfCollector.SignalR))
            Record("hub-connection", context.Hub.GetType().Name, exception is null ? "disconnect" : "disconnect-error", 0, exception is not null);
    }

    private void Record(string kind, string hub, string detail, double ms, bool error)
    {
        try
        {
            _recorder.Current.SpanFor(new SpanKey(kind, hub, detail)).Record(ms, error);
        }
        catch
        {
            // never affect the hub
        }
    }
}

/// Decorates the SignalR lifetime manager to count server-initiated
/// messages (broadcasts) per hub, method and target kind — the volume a
/// "push to everyone" pattern generates is invisible to the hub filter,
/// which only sees client → server calls. Every member delegates to the
/// framework's default manager unchanged; a reflection test pins that every
/// abstract/virtual member is overridden here.
public sealed class PerfHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
{
    private static readonly string HubName = typeof(THub).Name;
    private readonly HubLifetimeManager<THub> _inner;

    public PerfHubLifetimeManager(DefaultHubLifetimeManager<THub> inner) => _inner = inner;

    internal PerfHubLifetimeManager(HubLifetimeManager<THub> inner, bool _) => _inner = inner;

    private static void Count(string method, string target)
    {
        try
        {
            if (!PerfRuntime.IsOn(PerfCollector.SignalR)) return;
            PerfRuntime.Recorder!.Current.SpanFor(new SpanKey("broadcast", HubName, method + " → " + target)).Record(0);
        }
        catch
        {
            // never affect delivery
        }
    }

    public override Task OnConnectedAsync(HubConnectionContext connection) => _inner.OnConnectedAsync(connection);

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => _inner.OnDisconnectedAsync(connection);

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "all");
        return _inner.SendAllAsync(methodName, args, cancellationToken);
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        Count(methodName, "all");
        return _inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "connection");
        return _inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "connection");
        return _inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "group");
        return _inner.SendGroupAsync(groupName, methodName, args, cancellationToken);
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "group");
        return _inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        Count(methodName, "group");
        return _inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);
    }

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "user");
        return _inner.SendUserAsync(userId, methodName, args, cancellationToken);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Count(methodName, "user");
        return _inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
    }

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        _inner.AddToGroupAsync(connectionId, groupName, cancellationToken);

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        _inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

    public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken) =>
        _inner.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);

    public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result) =>
        _inner.SetConnectionResultAsync(connectionId, result);

    public override bool TryGetReturnType(string invocationId, [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? type) =>
        _inner.TryGetReturnType(invocationId, out type);
}
