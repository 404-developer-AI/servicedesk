using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Persistence.Companies;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Default implementation of <see cref="IZammadTicketResolver"/>. The
/// logic mirrors the inline body that lived inside the worker before
/// the v0.0.41 phase 3 polish — extracted so the new recheck endpoint
/// can re-evaluate a single record without duplicating the priority
/// rules / reason strings / snapshot layout.
public sealed class ZammadTicketResolver : IZammadTicketResolver
{
    private readonly IZammadApiClient _zammad;
    private readonly ICompanyRepository _companies;
    private readonly ILogger<ZammadTicketResolver> _logger;

    public ZammadTicketResolver(
        IZammadApiClient zammad,
        ICompanyRepository companies,
        ILogger<ZammadTicketResolver> logger)
    {
        _zammad = zammad;
        _companies = companies;
        _logger = logger;
    }

    public async Task<ZammadTicketResolveResult> ResolveAsync(
        long zammadTicketId,
        ZammadMappingDictionary dict,
        CancellationToken ct)
    {
        var reasons = new List<string>(4);
        var snapshot = new Dictionary<string, object?>
        {
            ["zammadTicketId"] = zammadTicketId,
        };

        ZammadTicket? ticket;
        try
        {
            ticket = await _zammad.GetTicketAsync(zammadTicketId, ct);
        }
        catch (ZammadApiException ex)
        {
            ticket = null;
            reasons.Add("zammad_fetch_failed:" + (ex.UpstreamErrorCode ?? "unknown"));
            snapshot["upstreamError"] = ex.Message;
        }

        if (ticket is null)
        {
            return new ZammadTicketResolveResult(
                ZammadTicketId: zammadTicketId,
                ZammadTicketNumber: null,
                ZammadTicketTitle: null,
                Result: ZammadImportRecordResult.Failed,
                UnresolvedReasons: reasons,
                Mapping: snapshot);
        }

        snapshot["zammadTicketNumber"] = ticket.Number;
        snapshot["zammadTicketTitle"] = ticket.Title;
        // Always surface the customer id even when contact lookup fails
        // so the Create-contact dialog can pull the upstream name via
        // GET /users/{id} without a second ticket fetch.
        if (ticket.CustomerId is not null) snapshot["zammadCustomerId"] = ticket.CustomerId;
        if (ticket.GroupName is not null) snapshot["zammadGroupName"] = ticket.GroupName;
        if (ticket.StateName is not null) snapshot["zammadStateName"] = ticket.StateName;
        if (ticket.PriorityName is not null) snapshot["zammadPriorityName"] = ticket.PriorityName;
        if (ticket.OrganizationName is not null) snapshot["zammadOrganizationName"] = ticket.OrganizationName;
        if (ticket.OrganizationId is not null) snapshot["zammadOrganizationId"] = ticket.OrganizationId;

        // --- mapping resolves -------------------------------------------
        Guid? queueId = null;
        if (ticket.GroupId is long groupId && dict.GroupToQueue.TryGetValue(groupId, out var qid))
        {
            queueId = qid;
            snapshot["queueId"] = qid;
            snapshot["zammadGroupId"] = groupId;
        }
        else
        {
            reasons.Add(ticket.GroupId is null
                ? "ticket_has_no_group"
                : $"group_unmapped:{ticket.GroupId}");
        }

        Guid? statusId = null;
        if (ticket.StateId is long stateId && dict.StateToStatus.TryGetValue(stateId, out var sid))
        {
            statusId = sid;
            snapshot["statusId"] = sid;
            snapshot["zammadStateId"] = stateId;
        }
        else
        {
            reasons.Add(ticket.StateId is null
                ? "ticket_has_no_state"
                : $"state_unmapped:{ticket.StateId}");
        }

        Guid? priorityId = null;
        if (ticket.PriorityId is long priId && dict.PriorityToPriority.TryGetValue(priId, out var pid))
        {
            priorityId = pid;
            snapshot["priorityId"] = pid;
            snapshot["zammadPriorityId"] = priId;
        }
        else
        {
            reasons.Add(ticket.PriorityId is null
                ? "ticket_has_no_priority"
                : $"priority_unmapped:{ticket.PriorityId}");
        }

        // --- contact lookup --------------------------------------------
        Guid? contactId = null;
        var contactEmail = ticket.CustomerEmail?.Trim();
        if (!string.IsNullOrEmpty(contactEmail))
        {
            snapshot["customerEmail"] = contactEmail;
            var existing = await _companies.GetContactByEmailAsync(contactEmail, ct);
            if (existing is not null)
            {
                contactId = existing.Id;
                snapshot["contactId"] = existing.Id;
            }
            else
            {
                reasons.Add("contact_not_found:" + contactEmail);
            }
        }
        else
        {
            reasons.Add("ticket_has_no_customer_email");
        }

        // --- result classification -------------------------------------
        // Priority order matches the schema's CHECK enum: contact (no
        // requester ⇒ nothing to import), then group/state/priority.
        string result;
        if (contactId is null)
            result = ZammadImportRecordResult.SkippedNoContact;
        else if (queueId is null)
            result = ZammadImportRecordResult.SkippedNoGroupMapping;
        else if (statusId is null)
            result = ZammadImportRecordResult.SkippedNoStateMapping;
        else if (priorityId is null)
            result = ZammadImportRecordResult.SkippedNoPriorityMapping;
        else
            result = ZammadImportRecordResult.Mapped;

        return new ZammadTicketResolveResult(
            ZammadTicketId: zammadTicketId,
            ZammadTicketNumber: ticket.Number?.ToString(),
            ZammadTicketTitle: ticket.Title,
            Result: result,
            UnresolvedReasons: reasons,
            Mapping: snapshot);
    }
}
