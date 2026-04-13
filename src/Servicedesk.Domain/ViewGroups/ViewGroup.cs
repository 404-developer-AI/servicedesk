namespace Servicedesk.Domain.ViewGroups;

public sealed record ViewGroup(
    Guid Id,
    string Name,
    string Description,
    int SortOrder,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ViewGroupMember(
    Guid UserId,
    string Email);

public sealed record ViewGroupView(
    Guid ViewId,
    string ViewName,
    int SortOrder);

public sealed record ViewGroupDetail(
    ViewGroup Group,
    IReadOnlyList<ViewGroupMember> Members,
    IReadOnlyList<ViewGroupView> Views);

public sealed record ViewGroupSummary(
    Guid Id,
    string Name,
    string Description,
    int SortOrder,
    int MemberCount,
    int ViewCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
