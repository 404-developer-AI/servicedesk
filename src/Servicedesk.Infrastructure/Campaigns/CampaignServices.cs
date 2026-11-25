using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;

namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — Campaigns registration (ARCHITECTURE.md → Campaigns).
internal static class CampaignServices
{
    public static IServiceCollection AddCampaignServices(this IServiceCollection services)
    {
        services.AddSingleton<ICampaignRepository, CampaignRepository>();
        services.AddSingleton<ICampaignRecipientRepository, CampaignRecipientRepository>();
        services.AddSingleton<ICampaignDashboardReader, CampaignDashboardReader>();
        services.AddSingleton<ICampaignOutboxRepository, CampaignOutboxRepository>();
        services.AddSingleton<CampaignSendSignal>();
        // The API replaces this with the SignalR notifier.
        services.TryAddSingleton<ICampaignNotifier, NullCampaignNotifier>();
        services.AddSingleton<ICampaignApprovalLinks, CampaignApprovalLinks>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<ICampaignAnswerService, CampaignAnswerService>();
        services.AddScoped<ICampaignSendService, CampaignSendService>();
        services.AddHostedService<CampaignSendWorker>();

        // Phase 3 — the maintenance planner.
        services.AddScoped<Tickets.ISystemTicketCreator, Tickets.SystemTicketCreator>();
        services.AddSingleton<Maintenance.IMaintenancePlanRepository, Maintenance.MaintenancePlanRepository>();
        services.AddSingleton<Maintenance.IMaintenanceRoundRepository, Maintenance.MaintenanceRoundRepository>();
        services.AddScoped<Maintenance.IMaintenanceRoundService, Maintenance.MaintenanceRoundService>();
        services.AddScoped<Maintenance.IMaintenanceReminderRepository, Maintenance.MaintenanceReminderRepository>();
        services.AddScoped<Maintenance.IMaintenanceNoteRepository, Maintenance.MaintenanceNoteRepository>();
        services.AddScoped<Maintenance.IMaintenanceReminderService, Maintenance.MaintenanceReminderService>();
        services.AddHostedService<Maintenance.MaintenancePlanWorker>();
        services.AddSingleton<MaintenancePlanSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<MaintenancePlanSearchSource>()));

        services.AddSingleton<CampaignSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<CampaignSearchSource>()));
        return services;
    }
}
