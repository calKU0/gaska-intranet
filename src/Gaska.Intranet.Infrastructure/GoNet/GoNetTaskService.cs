using FirebirdSql.Data.FirebirdClient;
using Gaska.Intranet.Core.Crm;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.GoNet;

/// <summary>
/// Contractor tasks with their correspondence, stored the way GoNet stores them:
/// <list type="bullet">
/// <item>task (ZADANIA) of a kind from the task-kinds dictionary (default "WIZYTA STARTOWA"), note in UWAGI (WIN1250);</item>
/// <item>for a task that already took place: a "Spotkanie" correspondence (KORESPONDENCJA + KORESPONDENCJADZIENNIK) with
/// the topic from the correspondence-topics dictionary (same name as the task kind) and the note as UTF-8 text;
/// the task points at it through IDKORESPONDENCJADZIENNIKROOT and is marked done.</item>
/// </list>
/// </summary>
internal sealed class GoNetTaskService(
    GoNetDatabase db,
    GoNetDictionary dictionary,
    IOptions<GoNetOptions> options,
    TimeProvider clock) : ICrmTaskService
{
    private readonly GoNetOptions _options = options.Value;

    public async Task<IReadOnlyList<CrmTask>> GetForContractorAsync(int contractorId, int take = 100, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = GoNetDatabase.Command(connection, null, $"""
            SELECT FIRST {Math.Clamp(take, 1, 500)}
                   z.ID, z.NUMERSTR, r.NAZWA AS RODZAJ, z.TERMINROZPOCZECIA, z.TERMINZAKONCZENIA, z.STATUS, z.UKONCZONO,
                   z.INFO, CAST(z.UWAGI AS BLOB SUB_TYPE 0) AS UWAGI, z.IDKORESPONDENCJADZIENNIKROOT AS ROOT,
                   zb.IMIE AS ZB_IMIE, zb.NAZWISKO AS ZB_NAZWISKO, zd.IMIE AS ZD_IMIE, zd.NAZWISKO AS ZD_NAZWISKO
            FROM ZADANIA z
            LEFT JOIN SLOWNIK r ON r.ID = z.IDRODZAJZADANIA
            LEFT JOIN OPERATOR zb ON zb.ID = z.IDZLECENIOBIORCY
            LEFT JOIN OPERATOR zd ON zd.ID = z.IDZLECENIODAWCY
            WHERE z.IDKONTRAHENTA = @id AND z.USUNIETY = 0
            ORDER BY z.TERMINROZPOCZECIA DESC, z.ID DESC
            """, ("@id", contractorId));

        var tasks = new List<(CrmTask Task, int RootId)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                tasks.Add((new CrmTask
                {
                    Id = GoNetDatabase.Int(reader, "ID"),
                    Number = GoNetDatabase.Text(reader, "NUMERSTR"),
                    Kind = GoNetDatabase.Text(reader, "RODZAJ"),
                    Start = GoNetDatabase.Date(reader, "TERMINROZPOCZECIA"),
                    End = GoNetDatabase.Date(reader, "TERMINZAKONCZENIA"),
                    Status = GoNetDatabase.Int(reader, "STATUS"),
                    PercentDone = GoNetDatabase.Int(reader, "UKONCZONO"),
                    Info = GoNetDatabase.Text(reader, "INFO"),
                    Notes = GoNetDatabase.Text(reader, "UWAGI"),
                    AssigneeName = Name(GoNetDatabase.Text(reader, "ZB_IMIE"), GoNetDatabase.Text(reader, "ZB_NAZWISKO")),
                    AuthorName = Name(GoNetDatabase.Text(reader, "ZD_IMIE"), GoNetDatabase.Text(reader, "ZD_NAZWISKO")),
                }, GoNetDatabase.Int(reader, "ROOT")));
            }
        }

        if (tasks.Count == 0)
        {
            return [];
        }

        var correspondence = await GetCorrespondenceAsync(connection, tasks.Select(t => t.Task.Id).ToList(), tasks.Where(t => t.RootId > 0).Select(t => t.RootId).ToList(), ct);
        return tasks
            .Select(t => t.Task with
            {
                Correspondence = correspondence
                    .Where(c => c.TaskId == t.Task.Id || (t.RootId > 0 && c.Entry.Id == t.RootId))
                    .Select(c => c.Entry)
                    .OrderBy(c => c.Date)
                    .ToList(),
            })
            .ToList();
    }

    public Task<int> AddTaskAsync(int contractorId, TaskInput input, int operatorId, CancellationToken ct = default) =>
        db.InTransactionAsync((connection, tx) => AddTaskAsync(connection, tx, contractorId, input, operatorId, ct), ct);

    /// <summary>Insert inside a caller-owned transaction (also used by the rollback-only GoNet integration test).</summary>
    internal async Task<int> AddTaskAsync(FbConnection connection, FbTransaction tx, int contractorId, TaskInput input, int operatorId, CancellationToken ct)
    {
        var now = clock.GetLocalNow().DateTime;
        var done = input.IsDone(now);
        var contactId = input.ContactId ?? -1;
        var topicId = input.TopicId ?? _options.StartVisitKindId;
        var topicName = await TopicNameAsync(connection, tx, topicId, ct)
            ?? throw new CrmException("Wybrany temat zadania nie istnieje w GoNet.");
        var notes = input.Notes?.Trim() ?? string.Empty;

        // Assignee: picked in the form (default: the operator adding the task); must be an active GoNet operator.
        var assigneeId = input.AssigneeId ?? operatorId;
        if (assigneeId != operatorId && (await dictionary.GetOperatorsAsync(ct)).All(o => o.Id != assigneeId))
        {
            throw new CrmException("Wybranej osoby nie ma na liście aktywnych operatorów GoNet – wybierz kogoś innego.");
        }

        // 1. Task (note in WIN1250 like all GoNet task notes).
        var taskNumber = await GoNetDatabase.NextIdAsync(connection, tx, "NR_ZADANIA", ct);
        await using var task = GoNetDatabase.Command(connection, tx, """
            EXECUTE PROCEDURE SP_DODAJZADANIE(
                @kontrahent, @adresat, @operator, @oddzial, 0, @rodzaj, @zleceniodawca, @zleceniobiorca, '', '',
                @start, @koniec, @info, @uwagi, @numer, @numerstr, @empty, -1, @ukonczono, @status, 0)
            """,
            ("@kontrahent", contractorId),
            ("@adresat", contactId),
            ("@operator", operatorId),
            ("@zleceniodawca", operatorId),
            ("@zleceniobiorca", assigneeId),
            ("@oddzial", _options.BranchId),
            ("@rodzaj", topicId),
            ("@start", input.Start),
            ("@koniec", input.End),
            ("@info", GoNetDatabase.Clean(string.IsNullOrWhiteSpace(input.Info) ? Capitalize(topicName) : input.Info, 250)),
            ("@uwagi", notes),
            ("@numer", taskNumber),
            ("@numerstr", $"{taskNumber}/{now:ddMMyyyy}"),
            ("@empty", GoNetDatabase.EmptyDate),
            ("@ukonczono", done ? 100 : 0),
            ("@status", done ? 2 : 0));
        var taskId = Convert.ToInt32(await task.ExecuteScalarAsync(ct));

        if (!done)
        {
            // Planned task – no correspondence yet.
            return taskId;
        }

        // 2. Correspondence note: topic from the correspondence-topics dictionary (added there when missing),
        //    text as UTF-8 – the procedure marks the record UTF = 1.
        var correspondenceTopicId = await dictionary.GetOrAddAsync(connection, tx, _options.CorrespondenceTopicDictionaryType, topicName, operatorId, ct);
        var correspondenceNumber = await GoNetDatabase.NextIdAsync(connection, tx, "NR_KORESPONDENCJI", ct);
        await using var note = GoNetDatabase.Command(connection, tx, """
            EXECUTE PROCEDURE SP_DODAJKORESPONDENCJA(
                @typakcji, @data, @temat, @operator, '', 0, @oddzial, 0, 0, 1, @kontrahent, @adresat, -1, 0,
                @uwagi, -1, -1, 0, @nr, -1, @empty, '', '', -1)
            """,
            ("@typakcji", _options.VisitCorrespondenceActionType),
            ("@data", input.Start),
            ("@temat", correspondenceTopicId),
            ("@operator", operatorId),
            ("@oddzial", _options.BranchId),
            ("@kontrahent", contractorId),
            ("@adresat", contactId),
            ("@uwagi", GoNetDatabase.EncodeUtf8(notes)),
            ("@nr", $"{correspondenceNumber}//{now:ddMMyyyy}"),
            ("@empty", GoNetDatabase.EmptyDate));
        var correspondenceId = Convert.ToInt32(await note.ExecuteScalarAsync(ct));

        await using var diaryQuery = GoNetDatabase.Command(connection, tx,
            "SELECT FIRST 1 ID FROM KORESPONDENCJADZIENNIK WHERE IDKORESPONDENCJI = @k ORDER BY ID", ("@k", correspondenceId));
        var diaryId = Convert.ToInt32(await diaryQuery.ExecuteScalarAsync(ct));

        // 3. Done task → link to the note, completion date and actual times (as GoNet does).
        await using var link = GoNetDatabase.Command(connection, tx,
            "UPDATE ZADANIA SET IDKORESPONDENCJADZIENNIKROOT = @diary, DATAUKONCZENIA = @done, FAKTSTART = @start, FAKTKONIEC = @koniec WHERE ID = @id",
            ("@diary", diaryId), ("@done", input.Start.Date), ("@start", input.Start), ("@koniec", input.End), ("@id", taskId));
        await link.ExecuteNonQueryAsync(ct);

        return taskId;
    }

    private static async Task<string?> TopicNameAsync(FbConnection connection, FbTransaction tx, int topicId, CancellationToken ct)
    {
        await using var command = GoNetDatabase.Command(connection, tx, "SELECT NAZWA FROM SLOWNIK WHERE ID = @id", ("@id", topicId));
        return (await command.ExecuteScalarAsync(ct))?.ToString()?.Trim() is { Length: > 0 } name ? name : null;
    }

    /// <summary>"WIZYTA STARTOWA" → "Wizyta startowa".</summary>
    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..].ToLowerInvariant();

    private static async Task<List<(int TaskId, CorrespondenceEntry Entry)>> GetCorrespondenceAsync(
        FbConnection connection, IReadOnlyList<int> taskIds, IReadOnlyList<int> diaryIds, CancellationToken ct)
    {
        // Ids are integers from the database – safe to inline (Firebird 2.5 has no array parameters).
        var conditions = new List<string> { $"d.IDZADANIEROOT IN ({string.Join(',', taskIds)})" };
        if (diaryIds.Count > 0)
        {
            conditions.Add($"d.ID IN ({string.Join(',', diaryIds)})");
        }

        await using var command = GoNetDatabase.Command(connection, null, $"""
            SELECT d.ID, d.IDZADANIEROOT, d.DATAAKCJI, CAST(d.UWAGI AS BLOB SUB_TYPE 0) AS UWAGI, k.TEMAT,
                   r.NAZWA AS RODZAJ, o.IMIE, o.NAZWISKO, c.KONTAKT
            FROM KORESPONDENCJADZIENNIK d
            JOIN KORESPONDENCJA k ON k.ID = d.IDKORESPONDENCJI
            LEFT JOIN SLOWNIK r ON r.ID = k.IDRODZAJAKCJI
            LEFT JOIN OPERATOR o ON o.ID = d.IDOPERATORA
            LEFT JOIN KONTAKTY c ON c.ID = d.IDADRESATA
            WHERE d.USUNIETY = 0 AND ({string.Join(" OR ", conditions)})
            """);

        var list = new List<(int, CorrespondenceEntry)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add((GoNetDatabase.Int(reader, "IDZADANIEROOT"), new CorrespondenceEntry
            {
                Id = GoNetDatabase.Int(reader, "ID"),
                Date = GoNetDatabase.Date(reader, "DATAAKCJI"),
                Kind = GoNetDatabase.Text(reader, "RODZAJ"),
                Subject = GoNetDatabase.Text(reader, "TEMAT"),
                Notes = GoNetDatabase.Text(reader, "UWAGI"),
                AuthorName = Name(GoNetDatabase.Text(reader, "IMIE"), GoNetDatabase.Text(reader, "NAZWISKO")),
                ContactName = GoNetDatabase.Text(reader, "KONTAKT"),
            }));
        }

        return list;
    }

    private static string? Name(string? first, string? last) =>
        $"{first} {last}".Trim() is { Length: > 0 } name ? name : null;
}
