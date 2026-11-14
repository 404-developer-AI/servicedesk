using Dapper;
using Servicedesk.Infrastructure.Notifications;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — the "Email me when I'm tagged" profile switch: only an explicit
/// "false" turns the tag mail off; no row (the default) keeps it on.
[Collection(PostgresCollection.Name)]
public sealed class MentionMailPreferenceDbTests
{
    private readonly PostgresDatabase _db;

    public MentionMailPreferenceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Only_users_who_switched_it_off_are_opted_out()
    {
        var seed = new DbSeed(_db.DataSource);
        var off = await seed.UserAsync("Agent");
        var on = await seed.UserAsync("Agent");
        var untouched = await seed.UserAsync("Agent");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("""
                INSERT INTO user_preferences (user_id, pref_key, pref_value)
                VALUES (@off, @key, 'false'), (@on, @key, 'true')
                """, new { off, on, key = NotificationPreferenceKeys.MentionEmail });
        }

        var optedOut = await new NotificationRepository(_db.DataSource)
            .ListMailOptOutsAsync(NotificationPreferenceKeys.MentionEmail, true, new[] { off, on, untouched }, default);

        Assert.Equal(new[] { off }, optedOut.ToArray());

        // A default-off switch (likes): only users who stored "true" get mail.
        var noLikeMail = await new NotificationRepository(_db.DataSource)
            .ListMailOptOutsAsync(NotificationPreferenceKeys.MentionEmail, false, new[] { off, on, untouched }, default);
        Assert.Equal(new[] { off, untouched }.OrderBy(x => x), noLikeMail.OrderBy(x => x));
    }
}
