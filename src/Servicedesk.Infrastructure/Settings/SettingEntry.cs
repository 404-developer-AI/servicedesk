namespace Servicedesk.Infrastructure.Settings;

public sealed record SettingEntry(
    string Key,
    string Value,
    string ValueType,
    string Category,
    string Description,
    string DefaultValue,
    DateTimeOffset UpdatedUtc);
