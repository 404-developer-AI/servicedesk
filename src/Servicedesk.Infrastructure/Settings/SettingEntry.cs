namespace Servicedesk.Infrastructure.Settings;

// UpdatedUtc is DateTime, not DateTimeOffset. Dapper materialises a
// positional record by matching ctor parameter types exactly; Npgsql maps
// Postgres `timestamptz` to DateTime and Dapper has no conversion step in
// the positional-record path. Same fix as AuditLogEntry.Utc in v0.0.3.
public sealed record SettingEntry(
    string Key,
    string Value,
    string ValueType,
    string Category,
    string Description,
    string DefaultValue,
    DateTime UpdatedUtc);
