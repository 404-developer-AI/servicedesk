using System.Reflection;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Interface fake that routes every call (including default interface
/// methods) through one handler — for wide interfaces where a test touches a
/// handful of members and any other call should fail loudly.
public class Fake<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[]?, object?> _handler = null!;

    public static T Create(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = Create<T, Fake<T>>();
        ((Fake<T>)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
}
