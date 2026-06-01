using Microsoft.Data.SqlClient;

namespace TimesheetMigrator;

/// Read-only access to the legacy MSSQL TimeSheet database. Every query is
/// a plain parameterless SELECT — this tool never writes to the source.
public sealed class SourceReader
{
    private readonly string _connectionString;

    public SourceReader(string connectionString) => _connectionString = connectionString;

    public async Task<List<string>> ReadTaskNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT name FROM dbo.task_model ORDER BY name", conn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            if (!rd.IsDBNull(0)) names.Add(rd.GetString(0));
        return names;
    }

    public async Task<List<SourceEmployee>> ReadEmployeesAsync(CancellationToken ct)
    {
        var rows = new List<SourceEmployee>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT id, given_name FROM dbo.employee_model", conn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            rows.Add(new SourceEmployee(
                rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1)));
        return rows;
    }

    public async Task<long> CountSlotsAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.time_slot_model", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// Streams the slot table in id-ordered pages so a 53K-row migration
    /// never holds the whole table in memory.
    public async IAsyncEnumerable<SourceSlot> ReadSlotsAsync(
        int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        long lastId = 0;
        while (true)
        {
            var page = await ReadSlotPageAsync(lastId, pageSize, ct);
            if (page.Count == 0) yield break;
            foreach (var slot in page) yield return slot;
            lastId = page[^1].Id;
            if (page.Count < pageSize) yield break;
        }
    }

    private async Task<List<SourceSlot>> ReadSlotPageAsync(long afterId, int pageSize, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (@take)
                   id, description, start_time, end_time, invoiced, task,
                   zammad_ticket_number, employee_id, created_by,
                   last_modified_by, created_date, modified_date
              FROM dbo.time_slot_model
             WHERE id > @afterId
             ORDER BY id ASC
            """;
        var rows = new List<SourceSlot>(pageSize);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@take", pageSize);
        cmd.Parameters.AddWithValue("@afterId", afterId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            rows.Add(new SourceSlot(
                Id: rd.GetInt64(0),
                Description: rd.IsDBNull(1) ? null : rd.GetString(1),
                StartTime: rd.GetDateTime(2),
                EndTime: rd.GetDateTime(3),
                Invoiced: rd.IsDBNull(4) ? null : rd.GetBoolean(4),
                Task: rd.IsDBNull(5) ? null : rd.GetString(5),
                ZammadTicketNumber: rd.IsDBNull(6) ? null : rd.GetString(6),
                EmployeeId: rd.IsDBNull(7) ? null : rd.GetString(7),
                CreatedBy: rd.IsDBNull(8) ? null : rd.GetString(8),
                LastModifiedBy: rd.IsDBNull(9) ? null : rd.GetString(9),
                CreatedDate: rd.IsDBNull(10) ? null : rd.GetDateTimeOffset(10),
                ModifiedDate: rd.IsDBNull(11) ? null : rd.GetDateTimeOffset(11)));
        }
        return rows;
    }
}
