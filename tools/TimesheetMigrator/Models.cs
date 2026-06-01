using System.Text.Json.Serialization;

namespace TimesheetMigrator;

// ---- Source rows (legacy MSSQL TimeSheet DB) ---------------------------

/// One row of dbo.time_slot_model. Times are datetime2 (local wall-clock,
/// no offset); the audit columns are datetimeoffset.
public sealed record SourceSlot(
    long Id,
    string? Description,
    DateTime StartTime,
    DateTime EndTime,
    bool? Invoiced,
    string? Task,
    string? ZammadTicketNumber,
    string? EmployeeId,
    string? CreatedBy,
    string? LastModifiedBy,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? ModifiedDate);

public sealed record SourceEmployee(string Id, string? GivenName);

// ---- Target catalogues (Servicedesk API) -------------------------------

public sealed record TargetTask(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("requiresTicket")] bool RequiresTicket,
    [property: JsonPropertyName("isAbsence")] bool IsAbsence,
    [property: JsonPropertyName("archived")] bool Archived);

public sealed record TargetUser(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("timesheetEnabled")] bool TimesheetEnabled);

public sealed record ListResponse<T>([property: JsonPropertyName("items")] List<T> Items);

// ---- Mapping file (operator-edited) ------------------------------------

public sealed class MappingFile
{
    [JsonPropertyName("importSource")]
    public string ImportSource { get; set; } = "legacy-mssql-timesheet";

    [JsonPropertyName("tasks")]
    public List<TaskMap> Tasks { get; set; } = new();

    [JsonPropertyName("employees")]
    public List<EmployeeMap> Employees { get; set; } = new();
}

public sealed class TaskMap
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("targetTaskId")] public Guid? TargetTaskId { get; set; }
    [JsonPropertyName("targetName")] public string? TargetName { get; set; }
}

public sealed class EmployeeMap
{
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("sourceName")] public string? SourceName { get; set; }
    [JsonPropertyName("targetUserId")] public Guid? TargetUserId { get; set; }
    [JsonPropertyName("targetEmail")] public string? TargetEmail { get; set; }
}

// ---- Import payload (matches the API's ImportRowDto, camelCase) ---------

public sealed record ImportRow(
    [property: JsonPropertyName("importRef")] string ImportRef,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("zammadTicketNumber")] string? ZammadTicketNumber,
    [property: JsonPropertyName("entryDate")] DateOnly EntryDate,
    [property: JsonPropertyName("startMinutes")] int StartMinutes,
    [property: JsonPropertyName("endMinutes")] int EndMinutes,
    [property: JsonPropertyName("invoiced")] bool Invoiced,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("createdByUserId")] Guid? CreatedByUserId,
    [property: JsonPropertyName("updatedByUserId")] Guid? UpdatedByUserId,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset? CreatedUtc,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset? UpdatedUtc);

public sealed record ImportBatch(
    [property: JsonPropertyName("importSource")] string ImportSource,
    [property: JsonPropertyName("rows")] List<ImportRow> Rows);

public sealed record ImportBatchResponse(
    [property: JsonPropertyName("received")] int Received,
    [property: JsonPropertyName("imported")] int Imported,
    [property: JsonPropertyName("withoutTicketMatch")] int WithoutTicketMatch,
    [property: JsonPropertyName("skipped")] List<SkipDetail>? Skipped);

public sealed record SkipDetail(
    [property: JsonPropertyName("importRef")] string ImportRef,
    [property: JsonPropertyName("reason")] string Reason);
