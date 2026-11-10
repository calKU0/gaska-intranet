using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Infrastructure.Helpdesk;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Options = Microsoft.Extensions.Options.Options;

namespace Gaska.Intranet.Tests;

public class HelpdeskTextTests
{
    [Theory]
    [InlineData("<p>Drukarka nie drukuje.</p><p>Od rana.</p>", "Drukarka nie drukuje.\nOd rana.")]
    [InlineData("&lt;p&gt;Zakodowane &amp;amp; raz jeszcze&lt;/p&gt;", "Zakodowane & raz jeszcze")]
    [InlineData("Linia 1<br>Linia 2<br/>Linia 3", "Linia 1\nLinia 2\nLinia 3")]
    [InlineData("<ul><li>raz</li><li>dwa</li></ul>", "• raz\n• dwa")]
    [InlineData("<p>ok</p><script>alert(1)</script><img src=x onerror=alert(2)>", "ok")]
    [InlineData(null, "")]
    public void Html_from_glpi_becomes_safe_plain_text(string? html, string expected) =>
        Assert.Equal(expected, HelpdeskText.ToPlainText(html));

    [Fact]
    public void Form_text_is_escaped_into_paragraphs()
    {
        var html = GlpiHelpdeskService.TextToHtml("Nie działa <b>VPN</b>\nod rana\n\nPozdrawiam");
        Assert.Equal("<p>Nie działa &lt;b&gt;VPN&lt;/b&gt;<br>od rana</p><p>Pozdrawiam</p>", html);
    }
}

public class GlpiMappingTests
{
    private static JsonObject Ticket() => JsonNode.Parse("""
        {
          "id": 812, "name": "Brak dostępu do VPN", "content": "<p>Opis</p>", "type": 1, "urgency": 4,
          "status": { "id": 5, "name": "Rozwiązane" }, "category": { "id": 3, "name": "Sieć > VPN" },
          "date": "2026-11-05 08:15:00", "date_mod": "2026-11-06 10:00:00",
          "team": [
            { "id": 7, "name": "Jan Kowalski", "type": "User", "role": "requester" },
            { "id": 9, "name": "Anna Nowak", "type": "User", "role": "observer" },
            { "id": 2, "name": "Technik", "type": "User", "role": "assigned" },
            { "id": 9, "name": "IT", "type": "Group", "role": "assigned" }
          ]
        }
        """)!.AsObject();

    [Fact]
    public void Ticket_is_mapped_with_the_users_role()
    {
        var requester = GlpiHelpdeskService.MapTicket(Ticket(), me: 7)!;
        Assert.Equal(812, requester.Id);
        Assert.Equal(TicketStatus.Solved, requester.Status);
        Assert.False(requester.IsOpen);
        Assert.Equal("Sieć > VPN", requester.Category);
        Assert.Equal(new DateTime(2026, 11, 5, 8, 15, 0), requester.OpenedAt);
        Assert.Equal(TicketRole.Requester, requester.MyRole);
        Assert.Equal(["Technik"], requester.Assignees.Select(a => a.Name));   // the "IT" group is not a person

        // Same id as a group – only the user membership counts.
        Assert.Equal(TicketRole.Observer, GlpiHelpdeskService.MapTicket(Ticket(), me: 9)!.MyRole);
        Assert.Equal(TicketRole.None, GlpiHelpdeskService.MapTicket(Ticket(), me: null)!.MyRole);
    }

    [Fact]
    public void Timeline_accepts_wrapped_and_flat_entries()
    {
        var json = JsonNode.Parse("""
            [
              { "type": "Followup", "item": { "id": 1, "content": "<p>Odp</p>", "user": { "id": 2, "name": "Technik" }, "date": "2026-11-05 09:00:00", "is_private": false } },
              { "type": "Followup", "item": { "id": 2, "content": "notatka", "is_private": true, "date": "2026-11-05 09:30:00" } },
              { "type": "Solution", "item": { "id": 3, "content": "Restart", "status": 2, "date_creation": "2026-11-05 10:00:00" } },
              { "itemtype": "Document_Item", "id": 4, "filename": "zrzut.png", "date_creation": "2026-11-05 11:00:00" },
              { "type": "Log", "item": { "id": 5 } }
            ]
            """);

        var items = GlpiHelpdeskService.ParseTimeline(json).ToList();

        Assert.Equal([TimelineKind.Followup, TimelineKind.Followup, TimelineKind.Solution, TimelineKind.Document], items.Select(i => i.Kind));
        Assert.Equal("Technik", items[0].Author);
        Assert.True(items[1].IsPrivate);
        Assert.Equal(SolutionStatus.Waiting, items[2].SolutionStatus);
        Assert.Equal("zrzut.png", items[3].FileName);
    }

    [Fact]
    public void Waiting_solution_is_offered_only_on_a_solved_ticket()
    {
        var solution = new TimelineItem { Kind = TimelineKind.Solution, Id = 3, SolutionStatus = SolutionStatus.Waiting };
        var solved = GlpiHelpdeskService.MapTicket(Ticket(), 7)!;

        Assert.Equal(3, new HelpdeskTicketDetails(solved, [solution]).PendingSolution?.Id);
        Assert.Null(new HelpdeskTicketDetails(solved with { Status = TicketStatus.Assigned }, [solution]).PendingSolution);
    }
}

public sealed class GlpiConnectorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeGlpi _glpi = new();
    private readonly GlpiConnector _connector;
    private readonly DirectoryUser _user = new() { AccountName = "jkowalski", Sid = "S-1-5-21-7" };

    public GlpiConnectorTests()
    {
        var options = Options.Create(new GlpiOptions { BaseUrl = "https://glpi.test", ClientId = "id", ClientSecret = "secret" });
        _connector = new GlpiConnector(new FakeHttpClientFactory(_glpi), _db, new EphemeralDataProtectionProvider(), options, _clock,
            NullLogger<GlpiConnector>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> StoredConnectionsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.GlpiConnections.CountAsync();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 250 && !await condition(); i++)   // up to 5 s – background work on a busy build machine
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Tokens_from_the_login_password_are_kept_only_after_the_second_factor()
    {
        _connector.BeginConnect(_user, "Haslo123!");
        await EventuallyAsync(() => Task.FromResult(_glpi.PasswordGrants == 1));
        await Task.Delay(50);
        Assert.Equal(0, await StoredConnectionsAsync());   // password ok, 2FA not done yet

        _connector.CompleteConnect(_user);
        await EventuallyAsync(async () => await StoredConnectionsAsync() == 1);

        Assert.True(await _connector.IsConnectedAsync(_user.Sid));
        var (token, glpiUserId) = await _connector.GetAccessTokenAsync(_user.Sid, CancellationToken.None);
        Assert.Equal("access-1", token);
        Assert.Equal(42, glpiUserId);

        await using var db = _db.CreateDbContext();
        Assert.DoesNotContain("access-1", (await db.GlpiConnections.SingleAsync()).ProtectedTokens);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_and_a_refused_refresh_disconnects()
    {
        Assert.Equal(ConnectResult.Connected, await _connector.ConnectAsync(_user, "Haslo123!"));

        _clock.Advance(TimeSpan.FromHours(2));
        var (token, _) = await _connector.GetAccessTokenAsync(_user.Sid, CancellationToken.None);
        Assert.Equal("access-2", token);
        Assert.Equal(1, _glpi.RefreshGrants);

        _clock.Advance(TimeSpan.FromHours(2));
        _glpi.RefuseRefresh = true;
        await Assert.ThrowsAsync<HelpdeskNotConnectedException>(() => _connector.GetAccessTokenAsync(_user.Sid, CancellationToken.None));
        Assert.Equal(0, await StoredConnectionsAsync());
    }

    [Fact]
    public async Task Wrong_password_is_reported_and_nothing_is_stored()
    {
        _glpi.RefusePassword = true;
        Assert.Equal(ConnectResult.WrongPassword, await _connector.ConnectAsync(_user, "zle"));
        Assert.False(await _connector.IsConnectedAsync(_user.Sid));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Minimal GLPI: /api.php/token (password + refresh grants) and /Administration/User/Me.</summary>
    private sealed class FakeGlpi : HttpMessageHandler
    {
        public int PasswordGrants;
        public int RefreshGrants;
        public bool RefusePassword;
        public bool RefuseRefresh;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Administration/User/Me", StringComparison.Ordinal))
            {
                return Json("""{ "id": 42, "username": "jkowalski" }""");
            }

            var form = await request.Content!.ReadAsStringAsync(ct);
            if (form.Contains("grant_type=password"))
            {
                Interlocked.Increment(ref PasswordGrants);
                return RefusePassword
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                    : Json("""{ "token_type": "Bearer", "expires_in": 3600, "access_token": "access-1", "refresh_token": "refresh-1" }""");
            }

            Interlocked.Increment(ref RefreshGrants);
            return RefuseRefresh || !form.Contains("refresh_token=refresh-1")
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                : Json("""{ "token_type": "Bearer", "expires_in": 3600, "access_token": "access-2" }""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

public sealed class GlpiHelpdeskServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeApi _api = new();
    private readonly GlpiHelpdeskService _service;
    private readonly GlpiConnector _connector;
    private readonly DirectoryUser _user = new() { AccountName = "jkowalski", Sid = "S-1-5-21-7" };

    public GlpiHelpdeskServiceTests()
    {
        var options = Options.Create(new GlpiOptions
        {
            BaseUrl = "https://glpi.test", ClientId = "id", ClientSecret = "secret",
            Attachments = new GlpiAttachmentOptions { UserToken = "svc-token", AppToken = "app-token", MaxFileMegabytes = 1 },
        });
        var http = new SingleHandlerFactory(_api);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero));
        _connector = new GlpiConnector(http, _db, new EphemeralDataProtectionProvider(), options, clock, NullLogger<GlpiConnector>.Instance);
        var uploader = new GlpiLegacyUploader(http, options, NullLogger<GlpiLegacyUploader>.Instance);
        _service = new GlpiHelpdeskService(http, _connector, uploader, new MemoryCache(new MemoryCacheOptions()), options, new Employees(),
            NullLogger<GlpiHelpdeskService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Deleted_tickets_are_not_listed_nor_opened()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        var tickets = await _service.GetMyTicketsAsync(_user.Sid);

        Assert.Equal([10], tickets.Select(t => t.Id));
        await Assert.ThrowsAsync<HelpdeskException>(() => _service.GetTicketAsync(_user.Sid, 9));
    }

    [Fact]
    public async Task Categories_follow_their_problem_and_request_settings()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        var incidents = await _service.GetCategoriesAsync(_user.Sid, TicketType.Incident);
        var requests = await _service.GetCategoriesAsync(_user.Sid, TicketType.Request);

        Assert.Equal(["Sprzęt", "Sprzęt > Drukarki"], incidents.Select(c => c.Name));
        Assert.Equal(["Sprzęt > Drukarki", "Dostępy"], requests.Select(c => c.Name));
    }

    [Fact]
    public async Task People_are_shown_with_their_AD_names()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        var ticket = (await _service.GetMyTicketsAsync(_user.Sid)).Single();

        Assert.Equal(["Joanna Nowak"], ticket.Requesters.Select(r => r.Name));
        Assert.Equal(["nieznany.login"], ticket.Assignees.Select(r => r.Name));   // not in AD → the login stays
    }

    [Fact]
    public async Task Request_bodies_are_sent_with_a_length_not_chunked()
    {
        // GLPI's web server never answers chunked requests – saves used to hang until the timeout.
        await _connector.ConnectAsync(_user, "Haslo123!");

        var result = await _service.CreateTicketAsync(_user.Sid, new NewTicketInput { Title = "Test", Description = "Opis", LocationId = 4 });

        Assert.Equal(13, result.Id);
        Assert.True(_api.CreateBodyHadLength);
        Assert.Contains("\"location\":{\"id\":4}", _api.CreateBody);
        // GLPI's API does not make the author the requester (its own form does).
        Assert.Contains(("POST", "/Assistance/Ticket/13/TeamMember", """{"type":"User","id":7,"role":"requester"}"""), _api.Calls);
    }

    [Theory]
    [InlineData("central", true)]
    [InlineData("helpdesk", false)]
    public async Task Technicians_are_recognised_by_their_GLPI_interface(string glpiInterface, bool technician)
    {
        await _connector.ConnectAsync(_user, "Haslo123!");
        _api.Interface = glpiInterface;

        var access = await _service.GetAccessAsync(_user.Sid);

        Assert.Equal(technician, access.IsTechnician);
        Assert.Equal(7, access.GlpiUserId);
        Assert.Equal("Joanna Nowak", access.Name);
    }

    [Fact]
    public async Task Status_urgency_and_assignees_are_changed_in_GLPI()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        await _service.UpdateTicketAsync(_user.Sid, 10, new TicketChanges { Status = TicketStatus.Waiting, Urgency = 5 });
        await _service.AddAssigneeAsync(_user.Sid, 10, 8);
        await _service.RemoveAssigneeAsync(_user.Sid, 10, 8);

        Assert.Contains(("PATCH", "/Assistance/Ticket/10", """{"status":{"id":4},"urgency":5}"""), _api.Calls);
        Assert.Contains(("POST", "/Assistance/Ticket/10/TeamMember", """{"type":"User","id":8,"role":"assigned"}"""), _api.Calls);
        Assert.Contains(("DELETE", "/Assistance/Ticket/10/TeamMember", """{"type":"User","id":8,"role":"assigned"}"""), _api.Calls);
    }

    [Fact]
    public async Task Private_notes_and_solutions_go_to_their_timeline_endpoints()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        await _service.AddEntryAsync(_user.Sid, 10, EntryKind.PrivateNote, "Tylko dla IT", []);
        await _service.AddEntryAsync(_user.Sid, 10, EntryKind.Solution, "Zrobione", []);

        Assert.Contains(("POST", "/Assistance/Ticket/10/Timeline/Followup", """{"content":"<p>Tylko dla IT</p>","is_private":true}"""), _api.Calls);
        Assert.Contains(("POST", "/Assistance/Ticket/10/Timeline/Solution", """{"content":"<p>Zrobione</p>"}"""), _api.Calls);
        await Assert.ThrowsAsync<HelpdeskException>(() => _service.AddEntryAsync(_user.Sid, 10, EntryKind.Solution, " ", []));
    }

    [Fact]
    public async Task Files_are_uploaded_by_the_service_account_after_the_users_own_access_check()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");
        var screenshot = new HelpdeskAttachment("zrzut ekranu.png", "image/png", [1, 2, 3]);

        var failed = await _service.AddEntryAsync(_user.Sid, 10, EntryKind.Reply, null, [screenshot]);

        Assert.Empty(failed);
        var paths = _api.Calls.Select(c => c.Method + " " + c.Path).ToList();
        var check = paths.IndexOf("POST /Assistance/Ticket/10/Timeline/Followup");
        Assert.True(check >= 0 && check < paths.IndexOf("GET /apirest.php/initSession"));
        Assert.Equal("user_token svc-token", _api.LegacyAuthorization);
        Assert.Equal("app-token", _api.LegacyAppToken);
        Assert.Contains("\"_filename\":[\"zrzut ekranu.png\"]", _api.UploadManifest);
        Assert.Contains("Joanna Nowak", _api.UploadManifest);
        // The files belong to the reply (GLPI shows them inside it), which was written with the user's own token.
        Assert.Contains(("POST", "/Assistance/Ticket/10/Timeline/Followup", """{"content":"<p>Załączniki: zrzut ekranu.png</p>","is_private":false}"""), _api.Calls);
        Assert.Contains(("POST", "/apirest.php/Document_Item/", """{"input":{"documents_id":55,"itemtype":"ITILFollowup","items_id":77}}"""), _api.Calls);
        Assert.Contains(("POST", "/apirest.php/changeActiveProfile", """{"profiles_id":6}"""), _api.Calls);   // Technician, not Super-Admin
        Assert.True(paths.IndexOf("POST /apirest.php/changeActiveEntities") < paths.IndexOf("POST /apirest.php/Document/"));
        Assert.Contains("GET /apirest.php/killSession", paths);
    }

    [Fact]
    public async Task Files_of_a_reply_are_shown_inside_it()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        var details = await _service.GetTicketAsync(_user.Sid, 10);

        var reply = details.Timeline.Single(i => i.Id == 77);
        Assert.Equal([new TicketDocument(55, "zrzut-ekranu.png")], reply.Documents);
        Assert.Empty(details.Timeline.Single(i => i.Id == 78).Documents);
    }

    [Fact]
    public async Task Nothing_is_uploaded_to_a_ticket_the_user_cannot_see()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");

        await Assert.ThrowsAsync<HelpdeskException>(() =>
            _service.AddEntryAsync(_user.Sid, 9999, EntryKind.Reply, null, [new HelpdeskAttachment("a.pdf", "application/pdf", [1])]));

        Assert.DoesNotContain(_api.Calls, c => c.Path.StartsWith("/apirest.php"));
    }

    [Fact]
    public async Task Too_large_files_are_refused_before_anything_is_sent()
    {
        await _connector.ConnectAsync(_user, "Haslo123!");
        var big = new HelpdeskAttachment("duzy.zip", "application/zip", new byte[1024 * 1024 + 1]);

        await Assert.ThrowsAsync<HelpdeskException>(() => _service.AddEntryAsync(_user.Sid, 10, EntryKind.Reply, "tekst", [big]));

        Assert.DoesNotContain(_api.Calls, c => c.Path.Contains("Followup"));
    }

    private sealed class Employees : IEmployeeDirectory
    {
        public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Employee>>(
                [new Employee { Id = "1", AccountName = "JNOWAK", FirstName = "Joanna", LastName = "Nowak" }]);

        public void InvalidateCache()
        {
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public bool CreateBodyHadLength;
        public string CreateBody = string.Empty;
        public string Interface = "helpdesk";
        public string? LegacyAuthorization;
        public string? LegacyAppToken;
        public string UploadManifest = string.Empty;
        public readonly List<(string Method, string Path, string? Body)> Calls = [];
        public int ActiveProfile = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/apirest.php/", StringComparison.Ordinal))
            {
                return await LegacyAsync(request, path, ct);
            }

            if (!path.EndsWith("/token", StringComparison.Ordinal))
            {
                var sent = request.Content is null ? null : Normalized(await request.Content.ReadAsStringAsync(ct));
                lock (Calls)
                {
                    Calls.Add((request.Method.Method, path.Replace("/api.php/v2.3", string.Empty), sent));
                }
            }

            if (path.Contains("/Ticket/9999"))
            {
                // A ticket the user cannot see.
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }

            if (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Delete
                || (request.Method == HttpMethod.Post && (path.Contains("/TeamMember") || path.Contains("/Timeline/"))))
            {
                var created = request.Method == HttpMethod.Post && path.Contains("/Timeline/") ? """{ "id": 77, "href": "/x" }""" : "{}";
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(created, Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/Assistance/Ticket"))
            {
                CreateBodyHadLength = request.Content!.Headers.ContentLength is > 0 && request.Headers.TransferEncodingChunked != true;
                CreateBody = await request.Content.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{ "id": 13 }""", Encoding.UTF8, "application/json") };
            }

            var body = path switch
            {
                _ when path.EndsWith("/token") => """{ "expires_in": 3600, "access_token": "a", "refresh_token": "r" }""",
                _ when path.EndsWith("/User/Me") => """{ "id": 7 }""",
                _ when path.EndsWith("/Assistance/Ticket") => """
                    [ { "id": 10, "name": "Aktywne", "is_deleted": false, "status": { "id": 1 },
                        "team": [ { "id": 7, "name": "jnowak", "type": "User", "role": "requester" },
                                  { "id": 3, "name": "IT", "type": "Group", "role": "assigned" },
                                  { "id": 8, "name": "nieznany.login", "type": "User", "role": "assigned" } ] },
                      { "id": 9, "name": "W koszu", "is_deleted": true, "status": { "id": 1 } } ]
                    """,
                _ when path.EndsWith("/Assistance/Ticket/9") => """{ "id": 9, "name": "W koszu", "is_deleted": true }""",
                _ when path.EndsWith("/Assistance/Ticket/10/Timeline") => """
                    [ { "type": "Followup", "item": { "id": 77, "content": "<p>test2</p>", "date": "2026-11-09 10:02:00" } },
                      { "type": "Followup", "item": { "id": 78, "content": "<p>bez plików</p>", "date": "2026-11-09 10:03:00" } } ]
                    """,
                _ when path.EndsWith("/Assistance/Ticket/10") => """{ "id": 10, "name": "Aktywne", "status": { "id": 2 } }""",
                _ when path.EndsWith("/session") => """{ "user_id": 7, "friendly_name": "Joanna Nowak", "active_profile": { "id": 4, "name": "Technik", "interface": "INTERFACE" } }""".Replace("INTERFACE", Interface),
                _ when path.EndsWith("/Dropdowns/ITILCategory") => """
                    [ { "id": 1, "completename": "Sprzęt", "is_helpdesk_visible": true, "is_incident_visible": true, "is_request_visible": false },
                      { "id": 2, "completename": "Sprzęt > Drukarki", "is_helpdesk_visible": true, "is_incident_visible": true, "is_request_visible": true },
                      { "id": 3, "completename": "Dostępy", "is_helpdesk_visible": 1, "is_incident_visible": 0, "is_request_visible": 1 },
                      { "id": 4, "completename": "Tylko dla techników", "is_helpdesk_visible": false, "is_incident_visible": true, "is_request_visible": true } ]
                    """,
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        /// <summary>Readable JSON (System.Text.Json escapes &lt; and &gt; as < – the same JSON, harder to assert on).</summary>
        private static string Normalized(string json) =>
            JsonNode.Parse(json)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? json;

        private async Task<HttpResponseMessage> LegacyAsync(HttpRequestMessage request, string path, CancellationToken ct)
        {
            string? body = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "uploadManifest")
                    {
                        UploadManifest = await part.ReadAsStringAsync(ct);
                    }
                }
            }
            else if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(ct);
            }

            lock (Calls)
            {
                Calls.Add((request.Method.Method, path, body));
            }

            if (path == "/apirest.php/initSession")
            {
                LegacyAuthorization = request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null;
                LegacyAppToken = request.Headers.TryGetValues("App-Token", out var app) ? app.Single() : null;
            }

            if (path == "/apirest.php/changeActiveProfile")
            {
                ActiveProfile = body!.Contains("\"profiles_id\":6") ? 6 : ActiveProfile;
            }

            var json = path switch
            {
                "/apirest.php/initSession" => """{ "session_token": "sess-1" }""",
                "/apirest.php/getActiveProfile" => ActiveProfile == 6
                    ? """{ "active_profile": { "id": 6, "name": "Technician", "interface": "central" } }"""
                    : """{ "active_profile": { "id": 1, "name": "Self-Service", "interface": "helpdesk" } }""",
                "/apirest.php/getMyProfiles" => """{ "myprofiles": [ { "id": 1, "name": "Self-Service" }, { "id": 4, "name": "Super-Admin" }, { "id": 6, "name": "Technician" } ] }""",
                "/apirest.php/Document/" => """{ "id": 55, "message": "" }""",
                "/apirest.php/Document_Item/" => """{ "id": 90, "message": "" }""",
                "/apirest.php/ITILFollowup/77/Document_Item" => """[ { "id": 90, "documents_id": 55, "itemtype": "ITILFollowup", "items_id": 77 } ]""",
                "/apirest.php/ITILFollowup/78/Document_Item" => "[]",
                "/apirest.php/Document/55" => """{ "id": 55, "name": "zrzut", "filename": "zrzut-ekranu.png", "mime": "image/png" }""",
                _ => "true",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}

public class GlpiAttachmentTests
{
    [Theory]
    [InlineData(@"C:\fakepath\raport.pdf", "raport.pdf")]
    [InlineData("zrzut \"1\".png", "zrzut _1_.png")]
    [InlineData("   ", "zalacznik")]
    public void File_names_are_made_safe(string name, string expected) =>
        Assert.Equal(expected, GlpiLegacyUploader.SafeFileName(name));

    [Fact]
    public void Timeline_documents_carry_the_document_id()
    {
        var json = JsonNode.Parse("""
            [ { "type": "Document", "item": { "id": 90, "document": { "id": 55, "name": "zrzut.png" }, "date": "2026-11-09 10:00:00" } } ]
            """);

        var item = GlpiHelpdeskService.ParseTimeline(json).Single();

        Assert.Equal(55, item.DocumentId);
        Assert.Equal("zrzut.png", item.FileName);
        Assert.True(item.IsImage);
    }
}
