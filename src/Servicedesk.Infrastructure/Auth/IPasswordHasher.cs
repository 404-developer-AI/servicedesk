namespace Servicedesk.Infrastructure.Auth;

/// Minimal password-hash interface. Produces a self-describing encoded string
/// (so parameter changes are detectable) and verifies in constant time.
public interface IPasswordHasher
{
    string Hash(string password);

    /// Returns <c>true</c> if <paramref name="password"/> matches
    /// <paramref name="encoded"/>. When <paramref name="rehashNeeded"/> is
    /// <c>true</c>, the caller should re-hash with the current parameters and
    /// persist the new value (transparent upgrade on login).
    bool Verify(string encoded, string password, out bool rehashNeeded);
}
