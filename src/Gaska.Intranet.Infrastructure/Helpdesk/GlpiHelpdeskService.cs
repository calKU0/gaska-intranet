using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Core.Organization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Helpdesk;

/// <summary>
/// GLPI High-level API v2 (/api.php/v2.x). Every request carries the signed-in user's own token, so GLPI applies
/// that user's profile: a self-service user sees and may do exactly what the GLPI web interface allows them.
/// </summary>
internal sealed class GlpiHelpdeskService(
    IHttpClientFactory httpClients,
    GlpiConnector connector,
    GlpiLegacyUploader uploader,
    IMemoryCache cache,
    IOptions<GlpiOptions> options,
    IEmployeeDirectory employees,
    ILogger<GlpiHelpdeskService> logger) : IHelpdeskService
{
    private static readonly TimeSpan AccessCacheTime = TimeSpan.FromMinutes(5);

    private GlpiOptions Options => options.Value;

    public bool CanAttachFiles => uploader.IsConfigured;

    public long MaxAttachmentBytes => Math.Max(1, Options.Attachments.MaxFileMegabytes) * 1024L * 1024L;

    public int MaxAttachments => Math.Max(1, Options.Attachments.MaxFiles);

    private string ApiRoot => $"{Options.BaseUrl.TrimEnd('/')}/api.php/{Options.ApiVersion}/";

    public string TicketUrl(int ticketId) => $"{Options.BaseUrl.TrimEnd('/')}/front/ticket.form.php?id={ticketId}";

    public async Task<IReadOnlyList<HelpdeskTicket>> GetMyTicketsAsync(string userSid, CancellationToken ct = default)
    {
        var (json, me) = await SendAsync(userSid, HttpMethod.Get,
            $"Assistance/Ticket?limit={Options.MaxTickets}&sort=date_mod:desc", null, ct);
        if (json is not JsonArray items)
        {
            return [];
        }

        var names = await FullNamesAsync(ct);
        return items.OfType<JsonObject>().Select(t => MapTicket(t, me)).OfType<HelpdeskTicket>()
            .Where(t => !t.IsDeleted)
            .Select(t => WithFullNames(t, names))
            .ToList();
    }

    /// <summary>
    /// GLPI's API gives people by their login ("jnowak"). GLPI logins are the AD account names, so the names come from
    /// the (cached) Active Directory employee list – no extra GLPI calls, and works for users without GLPI's user rights.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> FullNamesAsync(CancellationToken ct)
    {
        try
        {
            return (await employees.GetEmployeesAsync(ct))
                .GroupBy(e => e.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().FullName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AD names for helpdesk tickets unavailable – showing GLPI logins");
            return new Dictionary<string, string>();
        }
    }

    internal static HelpdeskTicket WithFullNames(HelpdeskTicket ticket, IReadOnlyDictionary<string, string> names) =>
        ticket with
        {
            Team = ticket.Team
                .Select(p => p.Type == "User" && names.TryGetValue(p.Name, out var full) && !string.IsNullOrWhiteSpace(full) ? p with { Name = full } : p)
                .ToList(),
        };

    public async Task<HelpdeskTicketDetails> GetTicketAsync(string userSid, int ticketId, CancellationToken ct = default)
    {
        var (ticketJson, me) = await SendAsync(userSid, HttpMethod.Get, $"Assistance/Ticket/{ticketId}", null, ct);
        var ticket = ticketJson is JsonObject obj ? MapTicket(obj, me) : null;
        if (ticket is null || ticket.IsDeleted)
        {
            throw new HelpdeskException("Nie znaleziono zgłoszenia albo nie masz do niego dostępu.");
        }

        var (timelineJson, _) = await SendAsync(userSid, HttpMethod.Get, $"Assistance/Ticket/{ticketId}/Timeline", null, ct);
        var names = await FullNamesAsync(ct);
        var timeline = ParseTimeline(timelineJson)
            .Select(i => i.Author is { } login && names.TryGetValue(login, out var full) ? i with { Author = full } : i)
            .OrderBy(i => i.Date ?? DateTime.MinValue)
            .ToList();
        return new HelpdeskTicketDetails(WithFullNames(ticket, names), await WithEntryDocumentsAsync(timeline, ct));
    }

    /// <summary>Adds the files attached to replies and solutions (API v2 lists only the ticket's own documents).</summary>
    private async Task<List<TimelineItem>> WithEntryDocumentsAsync(List<TimelineItem> timeline, CancellationToken ct)
    {
        if (!uploader.IsConfigured)
        {
            return timeline;
        }

        var entries = timeline
            .Where(i => i.Kind is TimelineKind.Followup or TimelineKind.Solution)
            .Select(i => (ItemType: EntryItemType(i.Kind), i.Id))
            .ToList();
        try
        {
            var documents = await uploader.GetEntryDocumentsAsync(entries, ct);
            return timeline
                .Select(i => i.Kind is TimelineKind.Followup or TimelineKind.Solution && documents.TryGetValue((EntryItemType(i.Kind), i.Id), out var docs)
                    ? i with { Documents = docs }
                    : i)
                .ToList();
        }
        catch (HelpdeskException ex)
        {
            logger.LogWarning(ex, "Reading documents of ticket messages failed – showing the messages without them");
            return timeline;
        }
    }

    private static string EntryItemType(TimelineKind kind) => kind == TimelineKind.Solution ? "ITILSolution" : "ITILFollowup";

    public async Task<IReadOnlyList<HelpdeskCategory>> GetCategoriesAsync(string userSid, TicketType type, CancellationToken ct = default)
    {
        // Filtered here, like GLPI's own form: a category can be limited to problems (incidents) or requests,
        // and hidden from the simplified interface altogether.
        var visibleFor = type == TicketType.Incident ? "is_incident_visible" : "is_request_visible";
        var (json, _) = await SendAsync(userSid, HttpMethod.Get, "Dropdowns/ITILCategory?limit=1000&sort=completename", null, ct);
        return json is JsonArray items
            ? items.OfType<JsonObject>()
                .Where(c => Bool(c, "is_helpdesk_visible") != false && Bool(c, visibleFor) != false)
                .Select(c => new HelpdeskCategory(Int(c, "id") ?? 0, Str(c, "completename") ?? Str(c, "name") ?? "?"))
                .Where(c => c.Id > 0)
                .ToList()
            : [];
    }

    public async Task<IReadOnlyList<HelpdeskLocation>> GetLocationsAsync(string userSid, CancellationToken ct = default)
    {
        var (json, _) = await SendAsync(userSid, HttpMethod.Get, "Dropdowns/Location?limit=1000&sort=completename", null, ct);
        return json is JsonArray items
            ? items.OfType<JsonObject>()
                .Select(l => new HelpdeskLocation(Int(l, "id") ?? 0, Str(l, "completename") ?? Str(l, "name") ?? "?"))
                .Where(l => l.Id > 0)
                .ToList()
            : [];
    }

    public async Task<IReadOnlyList<HelpdeskUser>> SearchUsersAsync(string userSid, string text, CancellationToken ct = default)
    {
        var term = new string(text.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_').ToArray());
        if (term.Length < 2)
        {
            return [];
        }

        // Every word must match the login, first or last name ("jan kow" finds Jan Kowalski).
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(w => $"(username=ilike=*{w}*,firstname=ilike=*{w}*,realname=ilike=*{w}*)");
        var filter = Uri.EscapeDataString("is_active==1;" + string.Join(';', words));
        var (json, _) = await SendAsync(userSid, HttpMethod.Get, $"Administration/User?filter={filter}&limit=20&sort=realname", null, ct);
        return json is JsonArray items
            ? items.OfType<JsonObject>()
                .Select(u => new HelpdeskUser(Int(u, "id") ?? 0, UserName(u), Str(u, "username") ?? string.Empty))
                .Where(u => u.Id > 0)
                .ToList()
            : [];
    }

    public Task AddObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        SendAsync(userSid, HttpMethod.Post, $"Assistance/Ticket/{ticketId}/TeamMember", TeamMember(glpiUserId, "observer"), ct);

    public Task RemoveObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        SendAsync(userSid, HttpMethod.Delete, $"Assistance/Ticket/{ticketId}/TeamMember", TeamMember(glpiUserId, "observer"), ct);

    public Task AddAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        SendAsync(userSid, HttpMethod.Post, $"Assistance/Ticket/{ticketId}/TeamMember", TeamMember(glpiUserId, "assigned"), ct);

    public Task RemoveAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        SendAsync(userSid, HttpMethod.Delete, $"Assistance/Ticket/{ticketId}/TeamMember", TeamMember(glpiUserId, "assigned"), ct);

    private async Task AddRequesterAsync(string userSid, int ticketId, int? me, CancellationToken ct)
    {
        try
        {
            var requesterId = me ?? (await GetAccessAsync(userSid, ct)).GlpiUserId;
            if (requesterId is not { } userId)
            {
                logger.LogWarning("GLPI ticket {Id} has no requester – the user's GLPI id is unknown", ticketId);
                return;
            }

            await SendAsync(userSid, HttpMethod.Post, $"Assistance/Ticket/{ticketId}/TeamMember", TeamMember(userId, "requester"), ct);
        }
        catch (HelpdeskException ex) when (ex is not HelpdeskNotConnectedException)
        {
            // The ticket exists – a missing requester must not turn the creation into an error (no duplicate on retry).
            logger.LogWarning(ex, "Adding the requester to GLPI ticket {Id} failed", ticketId);
        }
    }

    private static JsonObject TeamMember(int glpiUserId, string role) =>
        new() { ["type"] = "User", ["id"] = glpiUserId, ["role"] = role };

    /// <summary>
    /// GLPI's /session tells the active profile. "central" is GLPI's standard interface (technicians, administrators);
    /// "helpdesk" is the simplified self-service one. GLPI still checks every change against the profile's rights.
    /// </summary>
    public async Task<HelpdeskAccess> GetAccessAsync(string userSid, CancellationToken ct = default)
    {
        if (cache.TryGetValue(("glpi-access", userSid), out HelpdeskAccess? cached) && cached is not null)
        {
            return cached;
        }

        HelpdeskAccess access;
        try
        {
            var (json, me) = await SendAsync(userSid, HttpMethod.Get, "session", null, ct);
            access = ParseAccess(json as JsonObject, me);
        }
        catch (HelpdeskException ex) when (ex is not HelpdeskNotConnectedException)
        {
            logger.LogWarning(ex, "Reading the GLPI session failed – treating the user as self-service");
            return HelpdeskAccess.SelfService;
        }

        cache.Set(("glpi-access", userSid), access, AccessCacheTime);
        return access;
    }

    internal static HelpdeskAccess ParseAccess(JsonObject? session, int? me)
    {
        var profile = session?["active_profile"] as JsonObject;
        var name = Str(session, "friendly_name") ?? $"{Str(session, "first_name")} {Str(session, "real_name")}".Trim();
        return new HelpdeskAccess(
            Int(session, "user_id") is > 0 and var id ? id : me,
            string.IsNullOrWhiteSpace(name) ? Str(session, "name") : name,
            Str(profile, "name"),
            string.Equals(Str(profile, "interface"), "central", StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpdateTicketAsync(string userSid, int ticketId, TicketChanges changes, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (changes.Status is { } status)
        {
            body["status"] = new JsonObject { ["id"] = (int)status };
        }

        if (changes.Urgency is { } urgency)
        {
            body["urgency"] = Math.Clamp(urgency, 1, 5);
        }

        if (changes.CategoryId is { } categoryId)
        {
            // 0 = no category.
            body["category"] = new JsonObject { ["id"] = categoryId };
        }

        if (body.Count > 0)
        {
            await SendAsync(userSid, HttpMethod.Patch, $"Assistance/Ticket/{ticketId}", body, ct);
        }
    }

    public async Task<IReadOnlyList<string>> AddEntryAsync(string userSid, int ticketId, EntryKind kind, string? text,
        IReadOnlyList<HelpdeskAttachment> attachments, CancellationToken ct = default)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (!hasText && (attachments.Count == 0 || kind == EntryKind.Solution))
        {
            throw new HelpdeskException(kind == EntryKind.Solution ? "Opisz rozwiązanie." : "Wpisz treść odpowiedzi albo dodaj załącznik.");
        }

        CheckAttachments(attachments);

        // Files only: the message still carries them (GLPI shows documents inside the reply they belong to).
        var content = hasText ? TextToHtml(text!) : TextToHtml("Załączniki: " + string.Join(", ", attachments.Select(a => a.FileName)));
        var (path, itemType, body) = kind switch
        {
            EntryKind.Solution => ("Solution", "ITILSolution", new JsonObject { ["content"] = content }),
            _ => ("Followup", "ITILFollowup", new JsonObject { ["content"] = content, ["is_private"] = kind == EntryKind.PrivateNote }),
        };

        // Written with the user's own token – it also proves they may add to the ticket before the service account uploads.
        var (created, _) = await SendAsync(userSid, HttpMethod.Post, $"Assistance/Ticket/{ticketId}/Timeline/{path}", body, ct);
        var entryId = Int(created as JsonObject, "id");
        return await UploadAsync(userSid, ticketId, entryId is > 0 ? (itemType, entryId.Value) : null, attachments, ct);
    }

    private void CheckAttachments(IReadOnlyList<HelpdeskAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        if (!CanAttachFiles)
        {
            throw new HelpdeskException("Dodawanie załączników nie jest skonfigurowane.");
        }

        if (attachments.Count > MaxAttachments)
        {
            throw new HelpdeskException($"Można dodać najwyżej {MaxAttachments} plików naraz.");
        }

        if (attachments.FirstOrDefault(a => a.Content.LongLength > MaxAttachmentBytes) is { } tooBig)
        {
            throw new HelpdeskException($"Plik {tooBig.FileName} jest za duży (limit {Options.Attachments.MaxFileMegabytes} MB).");
        }
    }

    /// <summary>Called only after a request made with the user's own token reached the ticket.</summary>
    private async Task<IReadOnlyList<string>> UploadAsync(string userSid, int ticketId, (string ItemType, int Id)? entry,
        IReadOnlyList<HelpdeskAttachment> attachments, CancellationToken ct)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        var access = await GetAccessAsync(userSid, ct);
        return await uploader.UploadAsync(ticketId, entry, attachments, access.Name ?? "użytkownik intranetu", ct);
    }

    public async Task<HelpdeskFile> DownloadDocumentAsync(string userSid, int ticketId, int documentId, CancellationToken ct = default)
    {
        try
        {
            var (response, _) = await ExecuteAsync(userSid, HttpMethod.Get, $"Management/Document/{documentId}/Download", null, ct);
            using (response)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                var headers = response.Content.Headers;
                var name = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"');
                return new HelpdeskFile(
                    string.IsNullOrWhiteSpace(name) ? $"dokument-{documentId}" : name,
                    headers.ContentType?.MediaType ?? "application/octet-stream",
                    bytes);
            }
        }
        catch (HelpdeskException ex) when (ex is not HelpdeskNotConnectedException && uploader.IsConfigured)
        {
            // GLPI may not let a requester download a file linked to a reply (or added by the service account). Serve it
            // only when it belongs to the ticket or to a message the user sees – both come from the user's own timeline.
            var details = await GetTicketAsync(userSid, ticketId, ct);
            var visible = details.Timeline.Any(i => i.DocumentId == documentId || i.Documents.Any(d => d.Id == documentId));
            if (!visible)
            {
                throw;
            }

            return await uploader.DownloadAsync(documentId, ct);
        }
    }

    private static string UserName(JsonObject u)
    {
        var name = $"{Str(u, "firstname")} {Str(u, "realname")}".Trim();
        return name.Length > 0 ? name : Str(u, "username") ?? "?";
    }

    public async Task<NewTicketResult> CreateTicketAsync(string userSid, NewTicketInput input, CancellationToken ct = default)
    {
        CheckAttachments(input.Attachments);
        var body = new JsonObject
        {
            ["name"] = input.Title.Trim(),
            ["content"] = TextToHtml(input.Description),
            ["type"] = (int)input.Type,
            ["urgency"] = Math.Clamp(input.Urgency, 1, 5),
        };
        if (input.CategoryId is { } categoryId)
        {
            body["category"] = new JsonObject { ["id"] = categoryId };
        }

        if (input.LocationId is { } locationId)
        {
            body["location"] = new JsonObject { ["id"] = locationId };
        }

        var (json, me) = await SendAsync(userSid, HttpMethod.Post, "Assistance/Ticket", body, ct);
        if ((json as JsonObject) is not { } created || Int(created, "id") is not { } id)
        {
            throw new HelpdeskException("GLPI nie zwrócił numeru utworzonego zgłoszenia.");
        }

        // GLPI's own form puts the author in as the requester; the API does not (only as "user_recipient"), which left
        // tickets without a requester – missing from "Moje" and without the requester's notifications.
        await AddRequesterAsync(userSid, id, me, ct);

        var notAdded = new List<string>();
        foreach (var observer in input.Observers.DistinctBy(o => o.Id))
        {
            try
            {
                await AddObserverAsync(userSid, id, observer.Id, ct);
            }
            catch (HelpdeskException ex) when (ex is not HelpdeskNotConnectedException)
            {
                logger.LogWarning(ex, "Adding observer {Observer} to GLPI ticket {Id} failed", observer.UserName, id);
                notAdded.Add(observer.Name);
            }
        }

        // The user has just created the ticket with their own token, so they can see it.
        IReadOnlyList<string> filesNotAdded;
        try
        {
            filesNotAdded = await UploadAsync(userSid, id, null, input.Attachments, ct);
        }
        catch (HelpdeskException ex) when (ex is not HelpdeskNotConnectedException)
        {
            logger.LogWarning(ex, "Attachments of new GLPI ticket {Id} failed", id);
            filesNotAdded = input.Attachments.Select(a => a.FileName).ToList();
        }

        return new NewTicketResult(id, notAdded) { AttachmentsNotAdded = filesNotAdded };
    }

    private async Task AddFollowupAsync(string userSid, int ticketId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new HelpdeskException("Wpisz treść odpowiedzi.");
        }

        await SendAsync(userSid, HttpMethod.Post, $"Assistance/Ticket/{ticketId}/Timeline/Followup",
            new JsonObject { ["content"] = TextToHtml(text) }, ct);
    }

    public async Task AnswerSolutionAsync(string userSid, int ticketId, int solutionId, bool accept, string? comment, CancellationToken ct = default)
    {
        if (!accept && string.IsNullOrWhiteSpace(comment))
        {
            throw new HelpdeskException("Napisz, dlaczego rozwiązanie nie pomogło – technik zobaczy to w zgłoszeniu.");
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            await AddFollowupAsync(userSid, ticketId, comment, ct);
        }

        await SendAsync(userSid, HttpMethod.Patch, $"Assistance/Ticket/{ticketId}/Timeline/Solution/{solutionId}",
            new JsonObject { ["status"] = (int)(accept ? SolutionStatus.Accepted : SolutionStatus.Refused) }, ct);
    }

    /// <summary>One API call as the user. Returns the parsed body and the user's GLPI id.</summary>
    private async Task<(JsonNode? Body, int? GlpiUserId)> SendAsync(string userSid, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var (response, glpiUserId) = await ExecuteAsync(userSid, method, path, body, ct);
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            try
            {
                return (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text), glpiUserId);
            }
            catch (JsonException ex)
            {
                throw new HelpdeskException("Helpdesk (GLPI) zwrócił niepoprawną odpowiedź.", ex);
            }
        }
    }

    /// <summary>One API call as the user; errors become <see cref="HelpdeskException"/>. The caller disposes the (successful) response.</summary>
    private async Task<(HttpResponseMessage Response, int? GlpiUserId)> ExecuteAsync(string userSid, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var (token, glpiUserId) = await connector.GetAccessTokenAsync(userSid, ct);
        var http = httpClients.CreateClient(GlpiConnector.HttpClientName);

        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.TryAddWithoutValidation("Accept-Language", Options.Language);
        if (path.EndsWith("/Download", StringComparison.Ordinal))
        {
            // A file, not JSON (the client's default Accept header).
            request.Headers.Accept.ParseAdd("*/*");
        }
        if (body is not null)
        {
            // A complete string body (with Content-Length). JsonContent would stream it "chunked", and GLPI's web server
            // never answers chunked requests – every save hung until the timeout and nothing was created.
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }

        // Reads should be quick; saves may take long – GLPI runs ticket rules and sends notification e-mails before it
        // answers. A write that times out may still have been saved, so the message says so (no blind retries → duplicates).
        var isWrite = method != HttpMethod.Get;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(isWrite ? Options.WriteTimeoutSeconds : Options.TimeoutSeconds));

        HttpResponseMessage response;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GLPI {Method} {Path} failed after {Seconds:0.0} s", method, path, started.Elapsed.TotalSeconds);
            throw new HelpdeskException(ex is TaskCanceledException && isWrite
                ? "Helpdesk (GLPI) nie odpowiedział na czas. Zmiana mogła zostać zapisana – sprawdź zgłoszenie, zanim spróbujesz ponownie."
                : "Helpdesk (GLPI) jest chwilowo niedostępny. Spróbuj za chwilę.", ex);
        }

        if (started.Elapsed > TimeSpan.FromSeconds(5))
        {
            logger.LogWarning("GLPI {Method} {Path} took {Seconds:0.0} s", method, path, started.Elapsed.TotalSeconds);
        }

        if (response.IsSuccessStatusCode)
        {
            return (response, glpiUserId);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await connector.DisconnectAsync(userSid, ct);
                throw new HelpdeskNotConnectedException();
            }

            logger.LogWarning("GLPI {Method} {Path} → {Status}: {Body}", method, path, (int)response.StatusCode,
                text.Length > 500 ? text[..500] : text);
            throw new HelpdeskException(response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Brak uprawnień w Helpdesku do tej operacji.",
                HttpStatusCode.NotFound => "Nie znaleziono zgłoszenia albo nie masz do niego dostępu.",
                HttpStatusCode.BadRequest => "Helpdesk odrzucił dane: " + (ErrorDetail(text) ?? "sprawdź formularz."),
                _ => $"Helpdesk (GLPI) odpowiedział błędem {(int)response.StatusCode}. Spróbuj za chwilę.",
            });
        }
    }

    internal static HelpdeskTicket? MapTicket(JsonObject t, int? me)
    {
        if (Int(t, "id") is not { } id)
        {
            return null;
        }

        var team = (t["team"] as JsonArray)?.OfType<JsonObject>()
            .Select(m => new TicketPerson(Int(m, "id") ?? 0, Str(m, "name") ?? "?", Str(m, "type") ?? "User", Str(m, "role") ?? string.Empty))
            .ToList() ?? [];

        var role = TicketRole.None;
        if (me is { } myId)
        {
            foreach (var member in team.Where(m => m.Id == myId && m.Type.Equals("User", StringComparison.OrdinalIgnoreCase)))
            {
                role |= member.Role switch
                {
                    "requester" => TicketRole.Requester,
                    "observer" => TicketRole.Observer,
                    "assigned" => TicketRole.Assigned,
                    _ => TicketRole.None,
                };
            }
        }

        var status = t["status"] as JsonObject;
        return new HelpdeskTicket
        {
            Id = id,
            Title = Str(t, "name") ?? $"Zgłoszenie {id}",
            ContentHtml = Str(t, "content") ?? string.Empty,
            Status = (TicketStatus)(Int(status, "id") ?? Int(t, "status") ?? 1),
            StatusName = Str(status, "name") ?? string.Empty,
            Type = (TicketType)(Int(t, "type") ?? 1),
            Urgency = Int(t, "urgency") ?? 3,
            Category = Str(t["category"] as JsonObject, "name"),
            CategoryId = Int(t["category"] as JsonObject, "id") is > 0 and var categoryId ? categoryId : null,
            Location = Str(t["location"] as JsonObject, "name"),
            OpenedAt = Date(t, "date") ?? Date(t, "date_creation"),
            UpdatedAt = Date(t, "date_mod"),
            SolvedAt = Date(t, "date_solve"),
            ClosedAt = Date(t, "date_close"),
            Team = team,
            MyRole = role,
            IsDeleted = Bool(t, "is_deleted") == true,
        };
    }

    /// <summary>
    /// The timeline endpoint returns entries like { "type": "Followup", "item": { … } }; flat items with an
    /// "itemtype" are accepted too (the format is not described in the API documentation).
    /// </summary>
    internal static IEnumerable<TimelineItem> ParseTimeline(JsonNode? json)
    {
        if (json is not JsonArray entries)
        {
            yield break;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            var item = entry["item"] as JsonObject ?? entry;
            var type = Str(entry, "type") ?? Str(item, "itemtype") ?? string.Empty;
            var kind = type switch
            {
                _ when type.Contains("Followup", StringComparison.OrdinalIgnoreCase) => TimelineKind.Followup,
                _ when type.Contains("Solution", StringComparison.OrdinalIgnoreCase) => TimelineKind.Solution,
                _ when type.Contains("Task", StringComparison.OrdinalIgnoreCase) => TimelineKind.Task,
                _ when type.Contains("Document", StringComparison.OrdinalIgnoreCase) => TimelineKind.Document,
                _ when type.Contains("Validation", StringComparison.OrdinalIgnoreCase) => TimelineKind.Validation,
                _ => TimelineKind.Other,
            };
            if (Int(item, "id") is not { } id || kind == TimelineKind.Other)
            {
                continue;
            }

            var author = item["user"] as JsonObject ?? item["requester"] as JsonObject;

            // Timeline documents are document links (Document_Item: { document: { id, name } }); a flat Document is accepted too.
            var document = item["document"] as JsonObject;
            int? documentId = kind != TimelineKind.Document ? null
                : Int(document, "id") ?? Int(item, "documents_id") ?? (Str(item, "filename") is not null ? id : null);
            yield return new TimelineItem
            {
                Kind = kind,
                Id = id,
                Date = Date(item, "date") ?? Date(item, "date_creation") ?? Date(item, "submission_date"),
                Author = Str(author, "name"),
                AuthorId = Int(author, "id"),
                ContentHtml = Str(item, "content") ?? Str(item, "submission_comment") ?? Str(item, "comment") ?? string.Empty,
                IsPrivate = item["is_private"]?.GetValueKind() == JsonValueKind.True,
                SolutionStatus = kind == TimelineKind.Solution && Int(item, "status") is { } s ? (SolutionStatus)s : null,
                FileName = kind == TimelineKind.Document ? Str(item, "filename") ?? Str(document, "name") ?? Str(item, "name") : null,
                DocumentId = documentId,
            };
        }
    }

    /// <summary>Plain text from the form → the HTML GLPI stores (escaped, paragraphs and line breaks kept).</summary>
    internal static string TextToHtml(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Trim()
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + string.Join("<br>", p.Split('\n').Select(WebUtility.HtmlEncode)) + "</p>");
        return string.Concat(paragraphs);
    }

    private static string? ErrorDetail(string body)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;
            return Str(json, "detail") ?? Str(json, "title") ?? Str(json, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject? o, string name) =>
        o?[name] is JsonValue v
            ? v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<long>(out var l) ? (int)l
            : v.TryGetValue<double>(out var d) ? (int)d
            : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p
            : null
            : null;

    private static bool? Bool(JsonObject? o, string name) =>
        o?[name] is JsonValue v
            ? v.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.GetValue<double>() != 0,
                JsonValueKind.String => v.GetValue<string>() is "1" or "true",
                _ => null,
            }
            : null;

    private static DateTime? Date(JsonObject? o, string name) =>
        Str(o, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}
