using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gaska.Intranet.Core.Helpdesk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Helpdesk;

/// <summary>
/// Uploads files to tickets through GLPI's legacy REST API (apirest.php) – API v2 has no file upload. Runs as the service
/// account from <see cref="GlpiAttachmentOptions"/>, so the caller must first check the user may see the ticket.
/// </summary>
internal sealed class GlpiLegacyUploader(
    IHttpClientFactory httpClients,
    IOptions<GlpiOptions> options,
    ILogger<GlpiLegacyUploader> logger)
{
    private GlpiOptions Options => options.Value;

    private string Root => $"{Options.BaseUrl.TrimEnd('/')}/apirest.php/";

    public bool IsConfigured => Options.Attachments.IsConfigured;

    /// <summary>
    /// Uploads the files and links them to the message (<paramref name="entry"/>: "ITILFollowup" / "ITILSolution" + id),
    /// so GLPI shows them inside it – or to the ticket itself when there is no message or GLPI refuses the message link.
    /// Returns the names of files that failed.
    /// </summary>
    /// <param name="addedBy">Shown in the document's comment – GLPI itself lists the service account as the author.</param>
    public async Task<IReadOnlyList<string>> UploadAsync(int ticketId, (string ItemType, int Id)? entry, IReadOnlyList<HelpdeskAttachment> files,
        string addedBy, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return [];
        }

        return await WithSessionAsync(async (http, session) =>
        {
            var failed = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    var documentId = await UploadDocumentAsync(http, session, file, addedBy, ticketId, ct);
                    try
                    {
                        await LinkAsync(http, session, documentId, entry ?? ("Ticket", ticketId), ct);
                    }
                    catch (HelpdeskException ex) when (entry is not null)
                    {
                        logger.LogWarning(ex, "Linking document {Id} to {Type} {Entry} failed – linking it to ticket {Ticket}",
                            documentId, entry.Value.ItemType, entry.Value.Id, ticketId);
                        await LinkOrDeleteAsync(http, session, documentId, ticketId, ct);
                    }
                    catch (HelpdeskException)
                    {
                        await DeleteAsync(http, session, documentId);
                        throw;
                    }
                }
                catch (HelpdeskException ex)
                {
                    logger.LogWarning(ex, "Attaching {File} to GLPI ticket {Ticket} failed", file.FileName, ticketId);
                    failed.Add(file.FileName);
                }
            }

            return (IReadOnlyList<string>)failed;
        }, ct);
    }

    private async Task LinkOrDeleteAsync(HttpClient http, string session, int documentId, int ticketId, CancellationToken ct)
    {
        try
        {
            await LinkAsync(http, session, documentId, ("Ticket", ticketId), ct);
        }
        catch (HelpdeskException)
        {
            // A document linked to nothing is useless clutter in GLPI – remove it (best effort).
            await DeleteAsync(http, session, documentId);
            throw;
        }
    }

    /// <summary>
    /// Documents linked to messages. API v2 lists only the ticket's own documents, not those of its replies, so they are
    /// read here. The caller passes only messages the user can see (they come from the user's own timeline).
    /// </summary>
    public async Task<IReadOnlyDictionary<(string ItemType, int Id), IReadOnlyList<TicketDocument>>> GetEntryDocumentsAsync(
        IReadOnlyCollection<(string ItemType, int Id)> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return new Dictionary<(string, int), IReadOnlyList<TicketDocument>>();
        }

        return await WithSessionAsync(async (http, session) =>
        {
            var links = await Task.WhenAll(entries.Select(async entry =>
            {
                using var request = Request(HttpMethod.Get, $"{entry.ItemType}/{entry.Id}/Document_Item", session);
                var json = await SendAsync(http, request, "Document_Item", ct) as JsonArray;
                return (Entry: entry, DocumentIds: json?.OfType<JsonObject>().Select(l => IntOf(l["documents_id"])).Where(id => id > 0).ToList() ?? []);
            }));

            var names = new Dictionary<int, string>();
            foreach (var documentId in links.SelectMany(l => l.DocumentIds).Distinct())
            {
                using var request = Request(HttpMethod.Get, $"Document/{documentId}", session);
                var document = await SendAsync(http, request, "Document", ct) as JsonObject;
                names[documentId] = document?["filename"]?.ToString() is { Length: > 0 } file ? file : document?["name"]?.ToString() ?? $"dokument-{documentId}";
            }

            return (IReadOnlyDictionary<(string, int), IReadOnlyList<TicketDocument>>)links
                .Where(l => l.DocumentIds.Count > 0)
                .ToDictionary(l => l.Entry, l => (IReadOnlyList<TicketDocument>)l.DocumentIds.Select(id => new TicketDocument(id, names[id])).ToList());
        }, ct);
    }

    /// <summary>Downloads a document as the service account – the caller must have checked the user may see it.</summary>
    public async Task<HelpdeskFile> DownloadAsync(int documentId, CancellationToken ct) =>
        await WithSessionAsync(async (http, session) =>
        {
            using var meta = Request(HttpMethod.Get, $"Document/{documentId}", session);
            var document = await SendAsync(http, meta, "Document", ct) as JsonObject;

            using var request = Request(HttpMethod.Get, $"Document/{documentId}", session);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Options.WriteTimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HelpdeskException("Nie udało się pobrać dokumentu z Helpdesku.");
            }

            return new HelpdeskFile(
                document?["filename"]?.ToString() ?? $"dokument-{documentId}",
                document?["mime"]?.ToString() ?? "application/octet-stream",
                await response.Content.ReadAsByteArrayAsync(ct));
        }, ct);

    /// <summary>One legacy API session as the service account, on a technician profile, closed afterwards.</summary>
    private async Task<T> WithSessionAsync<T>(Func<HttpClient, string, Task<T>> action, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new HelpdeskException("Dodawanie załączników nie jest skonfigurowane (Glpi:Attachments).");
        }

        var http = httpClients.CreateClient(GlpiConnector.HttpClientName);
        var session = await InitSessionAsync(http, ct);
        try
        {
            await UseTechnicianProfileAsync(http, session, ct);
            return await action(http, session);
        }
        finally
        {
            await KillSessionAsync(http, session);
        }
    }

    /// <summary>
    /// The legacy API logs in with the account's default profile – often Self-Service, which cannot attach files to other
    /// people's tickets. Switch to a profile with GLPI's standard interface (the configured one first, administrator
    /// profiles last) and to all entities, recursively.
    /// </summary>
    private async Task UseTechnicianProfileAsync(HttpClient http, string session, CancellationToken ct)
    {
        var active = await ActiveProfileAsync(http, session, ct);
        if (!IsCentral(active))
        {
            using var request = Request(HttpMethod.Get, "getMyProfiles", session);
            var profiles = ((await SendAsync(http, request, "getMyProfiles", ct) as JsonObject)?["myprofiles"] as JsonArray)?
                .OfType<JsonObject>()
                .Select(p => (Id: IntOf(p["id"]), Name: p["name"]?.ToString() ?? string.Empty))
                .Where(p => p.Id > 0 && p.Id != IntOf(active?["id"]))
                .OrderByDescending(p => p.Id == Options.Attachments.ProfileId)
                .ThenBy(p => p.Name.Contains("admin", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            foreach (var profile in profiles)
            {
                using var change = Request(HttpMethod.Post, "changeActiveProfile", session);
                change.Content = Json(new JsonObject { ["profiles_id"] = profile.Id });
                await SendAsync(http, change, "changeActiveProfile", ct);
                active = await ActiveProfileAsync(http, session, ct);
                if (IsCentral(active))
                {
                    break;
                }
            }

            if (!IsCentral(active))
            {
                throw new HelpdeskException("Konto serwisowe GLPI do załączników nie ma profilu technika (interfejs standardowy).");
            }
        }

        using var entities = Request(HttpMethod.Post, "changeActiveEntities", session);
        entities.Content = Json(new JsonObject { ["entities_id"] = "all", ["is_recursive"] = true });
        await SendAsync(http, entities, "changeActiveEntities", ct);
    }

    private async Task<JsonObject?> ActiveProfileAsync(HttpClient http, string session, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "getActiveProfile", session);
        return (await SendAsync(http, request, "getActiveProfile", ct) as JsonObject)?["active_profile"] as JsonObject;
    }

    private static bool IsCentral(JsonObject? profile) =>
        string.Equals(profile?["interface"]?.ToString(), "central", StringComparison.OrdinalIgnoreCase);

    private static int IntOf(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : 0;

    private static StringContent Json(JsonNode body) =>
        new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private async Task<string> InitSessionAsync(HttpClient http, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "initSession", session: null);
        request.Headers.TryAddWithoutValidation("Authorization", "user_token " + Options.Attachments.UserToken);
        var json = await SendAsync(http, request, "initSession", ct);
        return (json as JsonObject)?["session_token"]?.GetValue<string>() is { Length: > 0 } token
            ? token
            : throw new HelpdeskException("Helpdesk (GLPI) nie zwrócił sesji do wysłania załączników.");
    }

    private async Task<int> UploadDocumentAsync(HttpClient http, string session, HelpdeskAttachment file, string addedBy, int ticketId, CancellationToken ct)
    {
        var fileName = SafeFileName(file.FileName);
        var manifest = new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["name"] = fileName,
                ["comment"] = $"Dodane w intranecie przez: {addedBy} (zgłoszenie #{ticketId})",
                ["_filename"] = new JsonArray(fileName),
            },
        };

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(manifest.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), "uploadManifest");
        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type)
            ? type
            : new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "filename[0]", fileName);

        using var request = Request(HttpMethod.Post, "Document/", session);
        request.Content = content;
        var json = await SendAsync(http, request, "Document", ct);
        return (json as JsonObject)?["id"] is JsonValue id && id.TryGetValue<int>(out var value) && value > 0
            ? value
            : throw new HelpdeskException("Helpdesk (GLPI) nie przyjął pliku " + fileName + ".");
    }

    private async Task LinkAsync(HttpClient http, string session, int documentId, (string ItemType, int Id) target, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["input"] = new JsonObject { ["documents_id"] = documentId, ["itemtype"] = target.ItemType, ["items_id"] = target.Id },
        };
        using var request = Request(HttpMethod.Post, "Document_Item/", session);
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        await SendAsync(http, request, "Document_Item", ct);
    }

    private async Task DeleteAsync(HttpClient http, string session, int documentId)
    {
        try
        {
            using var request = Request(HttpMethod.Delete, $"Document/{documentId}?force_purge=true", session);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Options.TimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Unlinked GLPI document {Id} could not be removed ({Status}) – delete it in GLPI", documentId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Unlinked GLPI document {Id} could not be removed – delete it in GLPI", documentId);
        }
    }

    private async Task KillSessionAsync(HttpClient http, string session)
    {
        try
        {
            using var request = Request(HttpMethod.Get, "killSession", session);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Closing the GLPI legacy API session failed");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? session)
    {
        var request = new HttpRequestMessage(method, Root + path);
        if (!string.IsNullOrWhiteSpace(Options.Attachments.AppToken))
        {
            request.Headers.TryAddWithoutValidation("App-Token", Options.Attachments.AppToken);
        }

        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Session-Token", session);
        }

        return request;
    }

    private async Task<JsonNode?> SendAsync(HttpClient http, HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.WriteTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new HelpdeskException("Helpdesk (GLPI) nie odpowiedział przy wysyłaniu załącznika.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // The legacy API answers errors as ["ERROR_CODE", "message"].
                logger.LogWarning("GLPI legacy API {What} → {Status}: {Body}", what, (int)response.StatusCode, text.Length > 500 ? text[..500] : text);
                throw new HelpdeskException("Helpdesk (GLPI) odrzucił załącznik: " + (LegacyError(text) ?? $"błąd {(int)response.StatusCode}"));
            }

            try
            {
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new HelpdeskException("Helpdesk (GLPI) zwrócił niepoprawną odpowiedź przy załączniku.", ex);
            }
        }
    }

    private static string? LegacyError(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonArray { Count: >= 2 } error ? error[1]?.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>File name without path parts or characters GLPI / Windows would reject.</summary>
    internal static string SafeFileName(string name)
    {
        var clean = new string(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' or '\'' ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "zalacznik" : clean.Length > 120 ? clean[..80] + Path.GetExtension(clean) : clean;
    }
}
