using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Persistence.ViewGroups;
using Servicedesk.Infrastructure.Persistence.Views;
using Servicedesk.Infrastructure.Preferences;

namespace Servicedesk.Infrastructure.Persistence;

/// Registration of the core data repositories every ticket screen reads:
/// companies, tickets (core, timeline, projects, recents), views and the
/// queue/view access services on top of them, and per-user preferences.
/// Split out of <c>DependencyInjection</c> to keep that file within its
/// size budget.
internal static class CoreRepositories
{
    public static IServiceCollection AddCoreRepositories(this IServiceCollection services)
    {
        services.AddSingleton<ICompanyRepository, CompanyRepository>();
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddSingleton<ITicketTimelineReader, TicketTimelineReader>();
        services.AddSingleton<ITicketProjectRepository, TicketProjectRepository>();
        services.AddSingleton<IRecentTicketsService, RecentTicketsService>();
        services.AddSingleton<IViewRepository, ViewRepository>();
        services.AddSingleton<IViewGroupRepository, ViewGroupRepository>();
        services.AddSingleton<IQueueAccessService, QueueAccessService>();
        services.AddSingleton<IViewAccessService, ViewAccessService>();
        services.AddSingleton<IUserPreferenceStore, UserPreferenceStore>();
        return services;
    }
}
