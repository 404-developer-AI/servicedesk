using System.Text.Json;

namespace TimesheetMigrator;

static class Commands
{
    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };

    // ---- map -----------------------------------------------------------

    public static async Task<int> MapAsync(Options o, CancellationToken ct)
    {
        var reader = new SourceReader(o.RequireSource());
        using var client = new TargetClient(o.RequireTarget(), o.RequireToken());

        Console.WriteLine("Reading source catalogues (MSSQL)…");
        var sourceTasks = await reader.ReadTaskNamesAsync(ct);
        var sourceEmployees = await reader.ReadEmployeesAsync(ct);

        Console.WriteLine("Reading target catalogues (Servicedesk)…");
        var targetTasks = await client.GetTasksAsync(ct);
        var targetUsers = await client.GetUsersAsync(ct);

        var mapping = new MappingFile
        {
            Tasks = sourceTasks.Select(name =>
            {
                var hit = targetTasks.FirstOrDefault(t =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                return new TaskMap { Source = name, TargetTaskId = hit?.Id, TargetName = hit?.Name };
            }).ToList(),
            Employees = sourceEmployees.Select(e =>
            {
                var hit = GuessUser(e.GivenName, targetUsers);
                return new EmployeeMap
                {
                    SourceId = e.Id,
                    SourceName = e.GivenName,
                    TargetUserId = hit?.Id,
                    TargetEmail = hit?.Email,
                };
            }).ToList(),
        };

        if (File.Exists(o.MappingPath))
        {
            Console.WriteLine($"\n{o.MappingPath} already exists. Refusing to overwrite — " +
                "rename or delete it first so your hand-edits are never lost.");
            return 1;
        }
        await File.WriteAllTextAsync(o.MappingPath, JsonSerializer.Serialize(mapping, WriteJson), ct);

        var taskGaps = mapping.Tasks.Where(t => t.TargetTaskId is null).Select(t => t.Source).ToList();
        var empGaps = mapping.Employees.Where(e => e.TargetUserId is null)
            .Select(e => $"{e.SourceName ?? "(no name)"} [{e.SourceId}]").ToList();

        Console.WriteLine($"\nWrote {o.MappingPath}");
        Console.WriteLine($"  Tasks     : {mapping.Tasks.Count - taskGaps.Count}/{mapping.Tasks.Count} auto-matched");
        Console.WriteLine($"  Employees : {mapping.Employees.Count - empGaps.Count}/{mapping.Employees.Count} auto-matched");
        if (taskGaps.Count > 0)
            Console.WriteLine($"  ⚠ Unmatched tasks (fill targetTaskId): {string.Join(", ", taskGaps)}");
        if (empGaps.Count > 0)
            Console.WriteLine($"  ⚠ Unmatched employees (fill targetUserId): {string.Join("; ", empGaps)}");
        Console.WriteLine("\nEdit the file, then run: timesheet-migrator import --dry-run");
        return 0;
    }

    /// Best-guess match of a legacy given-name onto a target user by the
    /// local-part of their email. Exact local-part match wins; otherwise a
    /// unique prefix match; otherwise null (operator fills it in).
    private static TargetUser? GuessUser(string? givenName, List<TargetUser> users)
    {
        var name = givenName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return null;

        string Local(TargetUser u) => u.Email.Split('@', 2)[0].ToLowerInvariant();

        var exact = users.Where(u => Local(u) == name).ToList();
        if (exact.Count == 1) return exact[0];

        var prefix = users.Where(u => Local(u).StartsWith(name, StringComparison.Ordinal)).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    // ---- import --------------------------------------------------------

    public static async Task<int> ImportAsync(Options o, CancellationToken ct)
    {
        if (!File.Exists(o.MappingPath))
        {
            Console.Error.WriteLine($"{o.MappingPath} not found. Run `map` first.");
            return 1;
        }
        var mapping = JsonSerializer.Deserialize<MappingFile>(await File.ReadAllTextAsync(o.MappingPath, ct))
            ?? throw new InvalidOperationException("Could not parse mapping file.");

        var taskByName = mapping.Tasks
            .Where(t => t.TargetTaskId is not null)
            .ToDictionary(t => t.Source, t => t.TargetTaskId!.Value, StringComparer.OrdinalIgnoreCase);
        var userByEmployee = mapping.Employees
            .Where(e => e.TargetUserId is not null)
            .ToDictionary(e => e.SourceId, e => e.TargetUserId!.Value, StringComparer.OrdinalIgnoreCase);

        TimeZoneInfo sourceTz;
        try
        {
            sourceTz = TimeZoneInfo.FindSystemTimeZoneById(o.SourceTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            Console.Error.WriteLine($"Unknown --source-tz / TS_SOURCE_TZ: '{o.SourceTimeZoneId}'. " +
                "Use an IANA id such as Europe/Brussels.");
            return 1;
        }

        var reader = new SourceReader(o.RequireSource());
        using var client = new TargetClient(o.RequireTarget(), o.RequireToken());

        var total = await reader.CountSlotsAsync(ct);
        Console.WriteLine($"{(o.DryRun ? "[DRY RUN] " : "")}Importing {total:N0} slots → {o.Target}");
        Console.WriteLine($"Import source key: {mapping.ImportSource}");
        Console.WriteLine($"Source time zone : {sourceTz.Id} (UTC start/end converted to this zone, DST-aware)\n");

        var stats = new Stats();
        var batch = new List<ImportRow>(o.BatchSize);

        await foreach (var slot in reader.ReadSlotsAsync(o.BatchSize, ct))
        {
            stats.Read++;
            var row = Transform(slot, taskByName, userByEmployee, sourceTz, stats);
            if (row is null) continue;
            batch.Add(row);

            if (batch.Count >= o.BatchSize)
            {
                await FlushAsync(client, mapping.ImportSource, batch, stats, o.DryRun, ct);
                batch.Clear();
                Console.Write($"\r  read {stats.Read:N0}/{total:N0} · imported {stats.Imported:N0} · skipped {stats.SkippedTotal():N0}   ");
            }
        }
        if (batch.Count > 0)
            await FlushAsync(client, mapping.ImportSource, batch, stats, o.DryRun, ct);

        Console.WriteLine("\n\n── Summary ──────────────────────────────────");
        Console.WriteLine($"  Read from source     : {stats.Read:N0}");
        Console.WriteLine($"  Imported (upserted)  : {stats.Imported:N0}{(o.DryRun ? " (would import)" : "")}");
        Console.WriteLine($"  Without ticket match : {stats.WithoutTicketMatch:N0} (linked empty, by design)");
        Console.WriteLine($"  Skipped — client side:");
        Console.WriteLine($"      unmapped task    : {stats.UnmappedTask:N0}");
        Console.WriteLine($"      unmapped employee: {stats.UnmappedEmployee:N0}");
        Console.WriteLine($"      crosses midnight : {stats.CrossesMidnight:N0}");
        Console.WriteLine($"      bad time window  : {stats.BadTimeWindow:N0}");
        Console.WriteLine($"  Skipped — server side: {stats.ServerSkipped:N0}");
        if (stats.ServerSkipExamples.Count > 0)
            Console.WriteLine($"      e.g. {string.Join(", ", stats.ServerSkipExamples.Take(5).Select(s => $"{s.ImportRef}:{s.Reason}"))}");
        if (o.DryRun)
            Console.WriteLine("\n[DRY RUN] Nothing was sent. Re-run without --dry-run to import.");
        return 0;
    }

    private static ImportRow? Transform(
        SourceSlot slot,
        Dictionary<string, Guid> taskByName,
        Dictionary<string, Guid> userByEmployee,
        TimeZoneInfo sourceTz,
        Stats stats)
    {
        if (slot.Task is null || !taskByName.TryGetValue(slot.Task, out var taskId))
        {
            stats.UnmappedTask++;
            return null;
        }
        if (slot.EmployeeId is null || !userByEmployee.TryGetValue(slot.EmployeeId, out var userId))
        {
            stats.UnmappedEmployee++;
            return null;
        }

        // start_time / end_time are UTC wall-clock stored in offset-less
        // datetime2 columns (the legacy app applied the local offset only on
        // display). Convert to the operator's zone — DST-aware, so the same
        // run handles both winter (+1) and summer (+2) slots — before
        // deriving the target's single-day (entry_date + minutes) values.
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(slot.StartTime, DateTimeKind.Utc), sourceTz);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(slot.EndTime, DateTimeKind.Utc), sourceTz);

        // A slot that spans past midnight (in local time) can't be expressed
        // in the single-day model — report and skip.
        if (endLocal.Date != startLocal.Date)
        {
            stats.CrossesMidnight++;
            return null;
        }

        var startMinutes = (int)startLocal.TimeOfDay.TotalMinutes;
        var endMinutes = (int)endLocal.TimeOfDay.TotalMinutes;
        if (startMinutes is < 0 or > 1440 || endMinutes is < 0 or > 1440 || endMinutes <= startMinutes)
        {
            stats.BadTimeWindow++;
            return null;
        }

        Guid? MapOptional(string? employeeId) =>
            employeeId is not null && userByEmployee.TryGetValue(employeeId, out var g) ? g : null;

        var zammad = slot.ZammadTicketNumber?.Trim();
        return new ImportRow(
            ImportRef: slot.Id.ToString(),
            UserId: userId,
            TaskId: taskId,
            ZammadTicketNumber: string.IsNullOrEmpty(zammad) ? null : zammad,
            EntryDate: DateOnly.FromDateTime(startLocal),
            StartMinutes: startMinutes,
            EndMinutes: endMinutes,
            Invoiced: slot.Invoiced ?? false,
            Description: slot.Description ?? "",
            CreatedByUserId: MapOptional(slot.CreatedBy),
            UpdatedByUserId: MapOptional(slot.LastModifiedBy),
            CreatedUtc: slot.CreatedDate,
            UpdatedUtc: slot.ModifiedDate);
    }

    private static async Task FlushAsync(
        TargetClient client, string source, List<ImportRow> batch, Stats stats, bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            // The server resolves ticket matches, so dry-run can't know the
            // ticket-match count; it only proves the transform produced a
            // well-formed batch. Count them as "would import".
            stats.Imported += batch.Count;
            return;
        }

        var resp = await client.PostBatchAsync(new ImportBatch(source, new List<ImportRow>(batch)), ct);
        stats.Imported += resp.Imported;
        stats.WithoutTicketMatch += resp.WithoutTicketMatch;
        var serverSkipped = resp.Received - resp.Imported;
        stats.ServerSkipped += serverSkipped;
        if (resp.Skipped is { Count: > 0 })
            foreach (var s in resp.Skipped)
                if (stats.ServerSkipExamples.Count < 20) stats.ServerSkipExamples.Add(s);
    }

    private sealed class Stats
    {
        public long Read;
        public long Imported;
        public long WithoutTicketMatch;
        public long UnmappedTask;
        public long UnmappedEmployee;
        public long CrossesMidnight;
        public long BadTimeWindow;
        public long ServerSkipped;
        public readonly List<SkipDetail> ServerSkipExamples = new();

        public long SkippedTotal() =>
            UnmappedTask + UnmappedEmployee + CrossesMidnight + BadTimeWindow + ServerSkipped;
    }
}
