using Microsoft.Extensions.DependencyInjection;

namespace Servicedesk.Infrastructure.Calendar;

/// v0.1.61 — Outlook calendar planning from forms (moved out of
/// DependencyInjection.cs; registrations unchanged).
internal static class CalendarServices
{
    public static IServiceCollection AddCalendarServices(this IServiceCollection services)
    {
        services.AddSingleton<IGraphCalendarClient, GraphCalendarClient>();
        services.AddSingleton<ICalendarSyncRepository, CalendarSyncRepository>();
        services.AddSingleton<CalendarSyncSignal>();
        services.AddScoped<ICalendarSyncService, CalendarSyncService>();
        services.AddHostedService<CalendarSyncWorker>();
        return services;
    }
}
