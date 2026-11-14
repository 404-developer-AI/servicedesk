using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The shared queue-access SQL rule: one predicate shape, one parameter
/// name, admin short-cut to a constant, and no way to smuggle SQL in via
/// the column argument.
public sealed class QueueAccessScopeSqlTests
{
    private static readonly Guid QueueA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

    [Fact]
    public void Agent_predicate_binds_the_shared_parameter()
    {
        var scope = new QueueAccessScope(false, new HashSet<Guid> { QueueA });
        Assert.Equal("t.queue_id = ANY(@AccessibleQueueIds)", scope.SqlPredicate("t.queue_id"));
    }

    [Fact]
    public void Admin_predicate_is_a_constant_true()
    {
        Assert.Equal("TRUE", QueueAccessScope.AdminScope.SqlPredicate("t.queue_id"));
        Assert.Equal("TRUE", QueueAccessScope.FromNullable(null).SqlPredicate("q.id"));
    }

    [Theory]
    [InlineData("t.queue_id; DROP TABLE tickets")]
    [InlineData("t.queue_id OR TRUE")]
    [InlineData("t.queue_id--")]
    [InlineData("")]
    public void Column_argument_must_be_a_plain_reference(string expression)
    {
        var scope = new QueueAccessScope(false, new HashSet<Guid> { QueueA });
        Assert.Throws<ArgumentException>(() => scope.SqlPredicate(expression));
    }

    [Fact]
    public void Parameters_carry_ids_and_admin_flag()
    {
        var scope = new QueueAccessScope(false, new HashSet<Guid> { QueueA });
        var p = scope.Parameters(new { Other = 1 });
        Assert.Equal(new[] { QueueA }, p.Get<Guid[]>(QueueAccessScope.QueueIdsParameter));
        Assert.False(p.Get<bool>(QueueAccessScope.IsAdminParameter));
    }

    [Fact]
    public void Agent_without_queues_binds_an_empty_array_never_null()
    {
        var scope = QueueAccessScope.FromNullable(Array.Empty<Guid>());
        Assert.False(scope.IsAdmin);
        Assert.Empty(scope.Parameters().Get<Guid[]>(QueueAccessScope.QueueIdsParameter));
    }

    [Fact]
    public void Search_principal_maps_to_the_same_scope()
    {
        Assert.True(QueueAccessScope.For(new SearchPrincipal(Guid.NewGuid(), "Admin", null)).IsAdmin);
        var agent = QueueAccessScope.For(new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { QueueA }));
        Assert.False(agent.IsAdmin);
        Assert.True(agent.CanSee(QueueA));
        Assert.False(QueueAccessScope.For(new SearchPrincipal(Guid.NewGuid(), "Agent", null)).CanSee(QueueA));
    }
}
