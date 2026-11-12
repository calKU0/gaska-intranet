using System.Collections.Concurrent;
using System.Text;
using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Infrastructure.Helpdesk;

/// <summary>
/// In-memory helpdesk for local development with the fake directory (ActiveDirectory:Provider=Fake) – lets the
/// Helpdesk pages be tried out and laid out without a GLPI account and without touching real tickets.
/// The sample data deliberately contains the awkward cases: long titles and texts, many observers, attachments,
/// private notes, a solution waiting for an answer. The fake "admin" is a technician (sees everything); other fake
/// users are regular employees and see only tickets they reported or observe.
/// </summary>
internal sealed class FakeHelpdesk : IHelpdeskService, IHelpdeskConnector
{
    private const int Me = 1;
    private readonly ConcurrentDictionary<int, (HelpdeskTicket Ticket, List<TimelineItem> Timeline)> _tickets = new();
    private int _nextId = 20;
    private int _nextItemId = 500;

    private static readonly HelpdeskUser[] Users =
    [
        new(Me, "Adam Administrator", "admin"),
        new(2, "Jan Kowalski", "jkowalski"),
        new(3, "Anna Nowak", "anowak"),
        new(4, "Katarzyna Kamińska", "kkaminska"),
        new(5, "Tomasz Lewandowski", "tlewandowski"),
        new(6, "Małgorzata Wiśniewska-Zielińska", "mwisniewska"),
    ];

    private static readonly HelpdeskCategory[] Categories =
    [
        new(1, "Sprzęt"), new(2, "Sprzęt > Drukarki i etykieciarki"), new(3, "Sieć > VPN i dostęp zdalny"),
        new(4, "Aplikacje własne > Automaty"), new(5, "Dostępy i uprawnienia"),
    ];

    public FakeHelpdesk()
    {
        var now = DateTime.Now;
        Add(new HelpdeskTicket
        {
            Id = 12, Title = "Drukarka etykiet w magazynie wysokiego składowania drukuje puste etykiety po wymianie taśmy",
            ContentHtml = "<p>Od wczoraj po wymianie taśmy drukarka Zebra ZT410 przy rampie nr 3 wysuwa puste etykiety.</p><p>Kalibracja z panelu nie pomogła.</p>",
            Status = TicketStatus.Assigned, Type = TicketType.Incident, Urgency = 4, Category = Categories[1].Name, CategoryId = 2,
            Location = "Magazyn > Hala B > Rampa 3", OpenedAt = now.AddDays(-1).AddHours(-3), UpdatedAt = now.AddMinutes(-25),
            Team = [Person(2, "requester"), Person(Me, "assigned"), Person(3, "observer"), Person(4, "observer"), Person(5, "observer"),
                    Person(6, "observer"), new TicketPerson(90, "IT", "Group", "assigned")],
        },
        [
            Item(TimelineKind.Followup, now.AddDays(-1), "Anna Nowak", "<p>U nas na rampie 2 to samo – dołączam zdjęcie.</p>",
                documents: [new TicketDocument(701, "rampa2-etykieta.jpg"), new TicketDocument(702, "log_drukarki_2026-11-08.txt")]),
            Item(TimelineKind.Followup, now.AddHours(-5), "Adam Administrator", "<p>Sprawdzam sterownik – proszę nie wyłączać drukarki.</p>", isPrivate: true),
            Item(TimelineKind.Task, now.AddHours(-2), "Adam Administrator", "<p>Aktualizacja firmware do V84.20.21Z.</p>"),
            Item(TimelineKind.Document, now.AddMinutes(-30), "Jan Kowalski", string.Empty, fileName: "bardzo_dluga_nazwa_pliku_ze_zrzutem_ekranu_konfiguracji_drukarki.png", documentId: 703),
        ]);

        Add(new HelpdeskTicket
        {
            Id = 11, Title = "Brak dostępu do VPN", ContentHtml = "<p>Po zmianie hasła nie mogę połączyć się z VPN z domu.</p>",
            Status = TicketStatus.Solved, Type = TicketType.Incident, Urgency = 3, Category = Categories[2].Name, CategoryId = 3,
            OpenedAt = now.AddDays(-3), UpdatedAt = now.AddHours(-4), SolvedAt = now.AddHours(-4),
            Team = [Person(2, "requester"), Person(5, "assigned")],
        },
        [
            Item(TimelineKind.Followup, now.AddDays(-2), "Tomasz Lewandowski", "<p>Proszę o zrzut ekranu z błędem.</p>"),
            Item(TimelineKind.Solution, now.AddHours(-4), "Tomasz Lewandowski",
                "<p>Zresetowałem certyfikat. Proszę uruchomić FortiClient ponownie i zalogować się nowym hasłem.</p>", solution: SolutionStatus.Waiting),
        ]);

        Add(new HelpdeskTicket
        {
            Id = 8, Title = "Nowe konto dla pracownika działu sprzedaży", ContentHtml = "<p>Proszę o konto AD, pocztę i dostęp do XL od 1.12.</p>",
            Status = TicketStatus.New, Type = TicketType.Request, Urgency = 2, Category = Categories[4].Name, CategoryId = 5,
            OpenedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-6), Team = [Person(4, "requester")],
        }, []);

        Add(new HelpdeskTicket
        {
            Id = 6, Title = "test", Status = TicketStatus.Closed, Type = TicketType.Incident, Urgency = 3,
            Category = Categories[3].Name, CategoryId = 4, OpenedAt = now.AddDays(-30), UpdatedAt = now.AddDays(-28),
            ClosedAt = now.AddDays(-28), Team = [Person(Me, "requester"), Person(Me, "assigned")],
        }, []);
    }

    // ----- IHelpdeskConnector: always connected -----
    public bool IsConfigured => true;

    public void BeginConnect(DirectoryUser user, string password)
    {
    }

    public void CompleteConnect(DirectoryUser user)
    {
    }

    public Task<bool> IsConnectedAsync(string userSid, CancellationToken ct = default) => Task.FromResult(true);

    public Task<ConnectResult> ConnectAsync(DirectoryUser user, string password, CancellationToken ct = default) =>
        Task.FromResult(ConnectResult.Connected);

    public Task DisconnectAsync(string userSid, CancellationToken ct = default) => Task.CompletedTask;

    // ----- IHelpdeskService -----
    public bool CanAttachFiles => true;
    public long MaxAttachmentBytes => 10 * 1024 * 1024;
    public int MaxAttachments => 5;

    public Task<IReadOnlyList<HelpdeskTicket>> GetMyTicketsAsync(string userSid, CancellationToken ct = default)
    {
        var me = UserOf(userSid);
        return Task.FromResult<IReadOnlyList<HelpdeskTicket>>(_tickets.Values
            .Where(t => CanSee(t.Ticket, me))
            .Select(t => WithRole(t.Ticket, me))
            .OrderByDescending(t => t.UpdatedAt).ToList());
    }

    public Task<HelpdeskTicketDetails> GetTicketAsync(string userSid, int ticketId, CancellationToken ct = default)
    {
        var me = UserOf(userSid);
        return _tickets.TryGetValue(ticketId, out var t) && CanSee(t.Ticket, me)
            ? Task.FromResult(new HelpdeskTicketDetails(WithRole(t.Ticket, me),
                t.Timeline.Where(i => !i.IsPrivate || me == Me).ToList()))
            : throw new HelpdeskException("Nie znaleziono zgłoszenia albo nie masz do niego dostępu.");
    }

    /// <summary>Fake SIDs end with 1000 + the fake employee number (FakeDirectory) – "admin" is 1.</summary>
    private static int UserOf(string sid) =>
        int.TryParse(sid.Split('-')[^1], out var n) && n > 1000 ? n - 1000 : Me;

    private static bool CanSee(HelpdeskTicket ticket, int me) => me == Me || ticket.Team.Any(p => p.Id == me && p.Type == "User");

    public Task<IReadOnlyList<HelpdeskCategory>> GetCategoriesAsync(string userSid, TicketType type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HelpdeskCategory>>(type == TicketType.Request ? Categories[1..] : Categories[..4]);

    public Task<IReadOnlyList<HelpdeskLocation>> GetLocationsAsync(string userSid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HelpdeskLocation>>(
            [new(4, "Biuro"), new(1, "Biuro > Parter"), new(2, "Biuro > Piętro 1 > Sala konferencyjna"), new(3, "Magazyn > Hala B > Rampa 3")]);

    public Task<NewTicketForm> GetNewTicketFormAsync(CancellationToken ct = default) =>
        Task.FromResult(new NewTicketForm(LocationRequired: true, DefaultLocation: "Biuro"));

    public Task<IReadOnlyList<HelpdeskUser>> SearchUsersAsync(string userSid, string text, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HelpdeskUser>>(Users
            .Where(u => u.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || u.UserName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public Task<NewTicketResult> CreateTicketAsync(string userSid, NewTicketInput input, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var now = DateTime.Now;
        Add(new HelpdeskTicket
        {
            Id = id, Title = input.Title, ContentHtml = $"<p>{System.Net.WebUtility.HtmlEncode(input.Description)}</p>",
            Status = TicketStatus.New, Type = input.Type, Urgency = input.Urgency, CategoryId = input.CategoryId,
            Category = Categories.FirstOrDefault(c => c.Id == input.CategoryId)?.Name, OpenedAt = now, UpdatedAt = now,
            Team = [Person(Me, "requester"), .. input.Observers.Select(o => Person(o.Id, "observer"))],
        }, input.Attachments.Select(a => Item(TimelineKind.Document, now, "Adam Administrator", string.Empty, fileName: a.FileName)).ToList());
        return Task.FromResult(new NewTicketResult(id, []));
    }

    public Task AddObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        ChangeTeam(ticketId, team => [.. team, Person(glpiUserId, "observer")]);

    public Task RemoveObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        ChangeTeam(ticketId, team => team.Where(p => !(p.Id == glpiUserId && p.Role == "observer")).ToList());

    public Task AddAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        ChangeTeam(ticketId, team => [.. team, Person(glpiUserId, "assigned")]);

    public Task RemoveAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default) =>
        ChangeTeam(ticketId, team => team.Where(p => !(p.Id == glpiUserId && p.Role == "assigned")).ToList());

    public Task<IReadOnlyList<string>> AddEntryAsync(string userSid, int ticketId, EntryKind kind, string? text,
        IReadOnlyList<HelpdeskAttachment> attachments, CancellationToken ct = default)
    {
        var t = Get(ticketId);
        var item = Item(kind == EntryKind.Solution ? TimelineKind.Solution : TimelineKind.Followup, DateTime.Now, "Adam Administrator",
            $"<p>{System.Net.WebUtility.HtmlEncode(text ?? string.Empty)}</p>", isPrivate: kind == EntryKind.PrivateNote,
            solution: kind == EntryKind.Solution ? SolutionStatus.Waiting : null,
            documents: attachments.Select(a => new TicketDocument(Interlocked.Increment(ref _nextItemId), a.FileName)).ToList());
        t.Timeline.Add(item);
        Update(ticketId, x => x with { UpdatedAt = DateTime.Now, Status = kind == EntryKind.Solution ? TicketStatus.Solved : x.Status });
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<HelpdeskAccess> GetAccessAsync(string userSid, CancellationToken ct = default)
    {
        var me = UserOf(userSid);
        var name = Users.FirstOrDefault(u => u.Id == me)?.Name ?? $"user{me}";
        return Task.FromResult(me == Me
            ? new HelpdeskAccess(me, name, "Technik (fake)", IsTechnician: true)
            : new HelpdeskAccess(me, name, "Self-Service (fake)", IsTechnician: false));
    }

    public Task UpdateTicketAsync(string userSid, int ticketId, TicketChanges changes, CancellationToken ct = default)
    {
        Update(ticketId, t => t with
        {
            Status = changes.Status ?? t.Status,
            Urgency = changes.Urgency ?? t.Urgency,
            CategoryId = changes.CategoryId ?? t.CategoryId,
            Category = changes.CategoryId is { } c ? Categories.FirstOrDefault(x => x.Id == c)?.Name : t.Category,
            UpdatedAt = DateTime.Now,
        });
        return Task.CompletedTask;
    }

    // Images get a small PNG (so previews can be checked), everything else a text file.
    private static readonly byte[] SamplePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAGAAAAAwCAIAAABhdOiYAAAAXklEQVR42u3QMQ0AAAjAMCxgCuG44sMGJF2moDGdp65jBSBAgAABAgQIECBAgAABAgQIECBAgAABAgQIECBAgAABAgQIECBAgAABAgQIECBAgAABAgQIECBAgAD9AFoKuo9adOQ76AAAAABJRU5ErkJggg==");

    public Task<HelpdeskFile> DownloadDocumentAsync(string userSid, int ticketId, int documentId, CancellationToken ct = default)
    {
        var name = _tickets.Values.SelectMany(t => t.Timeline)
            .SelectMany(i => i.Documents.Append(new TicketDocument(i.DocumentId ?? -1, i.FileName ?? string.Empty)))
            .FirstOrDefault(d => d.Id == documentId)?.FileName ?? $"dokument-{documentId}.txt";
        return Task.FromResult(HelpdeskAttachment.IsImageName(name)
            ? new HelpdeskFile(name, "image/png", SamplePng)
            : new HelpdeskFile(name, "text/plain", Encoding.UTF8.GetBytes("Plik testowy (fake helpdesk).")));
    }

    public Task AnswerSolutionAsync(string userSid, int ticketId, int solutionId, bool accept, string? comment, CancellationToken ct = default)
    {
        Update(ticketId, t => t with { Status = accept ? TicketStatus.Closed : TicketStatus.Assigned, UpdatedAt = DateTime.Now });
        return Task.CompletedTask;
    }

    public string TicketUrl(int ticketId) => $"https://helpdesk.example/front/ticket.form.php?id={ticketId}";

    // ----- helpers -----
    private void Add(HelpdeskTicket ticket, List<TimelineItem> timeline) => _tickets[ticket.Id] = (ticket, timeline);

    private (HelpdeskTicket Ticket, List<TimelineItem> Timeline) Get(int id) =>
        _tickets.TryGetValue(id, out var t) ? t : throw new HelpdeskException("Nie znaleziono zgłoszenia.");

    private void Update(int id, Func<HelpdeskTicket, HelpdeskTicket> change)
    {
        var t = Get(id);
        _tickets[id] = (change(t.Ticket), t.Timeline);
    }

    private Task ChangeTeam(int id, Func<IReadOnlyList<TicketPerson>, IReadOnlyList<TicketPerson>> change)
    {
        Update(id, t => t with { Team = change(t.Team), UpdatedAt = DateTime.Now });
        return Task.CompletedTask;
    }

    private static TicketPerson Person(int id, string role) =>
        new(id, Users.FirstOrDefault(u => u.Id == id)?.Name ?? $"user{id}", "User", role);

    private static HelpdeskTicket WithRole(HelpdeskTicket t, int me)
    {
        var role = TicketRole.None;
        foreach (var p in t.Team.Where(p => p.Id == me && p.Type == "User"))
        {
            role |= p.Role switch { "requester" => TicketRole.Requester, "observer" => TicketRole.Observer, "assigned" => TicketRole.Assigned, _ => 0 };
        }

        return t with { MyRole = role };
    }

    private TimelineItem Item(TimelineKind kind, DateTime date, string author, string html, bool isPrivate = false,
        SolutionStatus? solution = null, IReadOnlyList<TicketDocument>? documents = null, string? fileName = null, int? documentId = null) =>
        new()
        {
            Kind = kind, Id = Interlocked.Increment(ref _nextItemId), Date = date, Author = author, ContentHtml = html,
            IsPrivate = isPrivate, SolutionStatus = solution, Documents = documents ?? [], FileName = fileName, DocumentId = documentId,
        };
}
