using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Helpdesk;

/// <summary>GLPI ticket statuses (ids are fixed in GLPI).</summary>
public enum TicketStatus
{
    New = 1,
    Assigned = 2,
    Planned = 3,
    Waiting = 4,
    Solved = 5,
    Closed = 6,
}

public enum TicketType
{
    Incident = 1,
    Request = 2,
}

/// <summary>The current user's relation to a ticket.</summary>
[Flags]
public enum TicketRole
{
    None = 0,
    Requester = 1,
    Observer = 2,
    Assigned = 4,
}

public sealed record TicketPerson(int Id, string Name, string Type, string Role);

public sealed record HelpdeskTicket
{
    public required int Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Description as GLPI stores it (HTML).</summary>
    public string ContentHtml { get; init; } = string.Empty;

    public TicketStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public TicketType Type { get; init; }

    /// <summary>1 (very low) … 5 (very high).</summary>
    public int Urgency { get; init; }

    public string? Category { get; init; }
    public int? CategoryId { get; init; }
    public string? Location { get; init; }
    public DateTime? OpenedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? SolvedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public IReadOnlyList<TicketPerson> Team { get; init; } = [];

    /// <summary>In GLPI's trash – not shown in the intranet (GLPI's own lists hide it too).</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Filled for the signed-in user.</summary>
    public TicketRole MyRole { get; init; }

    public bool IsOpen => Status is not (TicketStatus.Solved or TicketStatus.Closed);

    // People only – assigned groups ("IT") and suppliers are not listed.
    public IEnumerable<TicketPerson> Requesters => People("requester");
    public IEnumerable<TicketPerson> Observers => People("observer");
    public IEnumerable<TicketPerson> Assignees => People("assigned");

    private IEnumerable<TicketPerson> People(string role) =>
        Team.Where(t => t.Role == role && t.Type.Equals("User", StringComparison.OrdinalIgnoreCase));
}

public enum TimelineKind
{
    Followup,
    Solution,
    Task,
    Document,
    Validation,
    Other,
}

/// <summary>Approval state of a proposed solution.</summary>
public enum SolutionStatus
{
    None = 1,
    Waiting = 2,
    Accepted = 3,
    Refused = 4,
}

public sealed record TimelineItem
{
    public required TimelineKind Kind { get; init; }
    public required int Id { get; init; }
    public DateTime? Date { get; init; }
    public string? Author { get; init; }
    public int? AuthorId { get; init; }
    public string ContentHtml { get; init; } = string.Empty;
    public bool IsPrivate { get; init; }

    /// <summary>Solutions only.</summary>
    public SolutionStatus? SolutionStatus { get; init; }

    /// <summary>Documents only – file name.</summary>
    public string? FileName { get; init; }

    /// <summary>Documents only – GLPI document id (for downloading).</summary>
    public int? DocumentId { get; init; }

    public bool IsImage => FileName is { } name && HelpdeskAttachment.IsImageName(name);

    /// <summary>Files attached to this reply / solution (GLPI shows them inside the message).</summary>
    public IReadOnlyList<TicketDocument> Documents { get; init; } = [];
}

/// <summary>A GLPI document linked to a ticket or to one of its messages.</summary>
public sealed record TicketDocument(int Id, string FileName)
{
    public bool IsImage => HelpdeskAttachment.IsImageName(FileName);
}

/// <summary>A file to attach to a ticket (picked from disk or pasted into the text box).</summary>
public sealed record HelpdeskAttachment(string FileName, string ContentType, byte[] Content)
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || IsImageName(FileName);

    public static bool IsImageName(string fileName) =>
        ImageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

/// <summary>A downloaded GLPI document.</summary>
public sealed record HelpdeskFile(string FileName, string ContentType, byte[] Content);

/// <summary>What the signed-in user's active GLPI profile allows.</summary>
public sealed record HelpdeskAccess(int? GlpiUserId, string? Name, string? ProfileName, bool IsTechnician)
{
    public static readonly HelpdeskAccess SelfService = new(null, null, null, false);
}

/// <summary>Ticket fields a technician can change; <c>null</c> = leave as it is.</summary>
public sealed record TicketChanges
{
    public TicketStatus? Status { get; init; }
    public int? Urgency { get; init; }
    public int? CategoryId { get; init; }
}

/// <summary>What a technician writes into the ticket.</summary>
public enum EntryKind
{
    /// <summary>Visible to the requester.</summary>
    Reply,

    /// <summary>Visible to technicians only.</summary>
    PrivateNote,

    /// <summary>Solves the ticket – the requester accepts or refuses it.</summary>
    Solution,
}

public sealed record HelpdeskCategory(int Id, string Name);

public sealed record HelpdeskLocation(int Id, string Name);

public sealed record NewTicketResult(int Id, IReadOnlyList<string> ObserversNotAdded)
{
    /// <summary>Files GLPI did not accept (the ticket itself was created).</summary>
    public IReadOnlyList<string> AttachmentsNotAdded { get; init; } = [];
}

/// <summary>A GLPI user (for adding observers).</summary>
public sealed record HelpdeskUser(int Id, string Name, string UserName);

public sealed record HelpdeskTicketDetails(HelpdeskTicket Ticket, IReadOnlyList<TimelineItem> Timeline)
{
    /// <summary>The newest solution waiting for the requester's answer (accept / refuse).</summary>
    public TimelineItem? PendingSolution =>
        Ticket.Status == TicketStatus.Solved
            ? Timeline.Where(t => t.Kind == TimelineKind.Solution && t.SolutionStatus == Helpdesk.SolutionStatus.Waiting)
                .MaxBy(t => t.Date ?? DateTime.MinValue)
            : null;
}

public sealed class NewTicketInput
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Podaj tytuł zgłoszenia.")]
    [System.ComponentModel.DataAnnotations.StringLength(250, ErrorMessage = "Tytuł może mieć najwyżej 250 znaków.")]
    public string Title { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Opisz problem lub potrzebę.")]
    [System.ComponentModel.DataAnnotations.StringLength(20000)]
    public string Description { get; set; } = string.Empty;

    public TicketType Type { get; set; } = TicketType.Incident;

    /// <summary>3 = medium (GLPI default).</summary>
    public int Urgency { get; set; } = 3;

    public int? CategoryId { get; set; }

    public int? LocationId { get; set; }

    /// <summary>People who get notifications about the ticket (GLPI "observers").</summary>
    public List<HelpdeskUser> Observers { get; set; } = [];

    public List<HelpdeskAttachment> Attachments { get; set; } = [];
}

/// <summary>GLPI said no, or is unreachable – the message is safe to show to the user (Polish).</summary>
public class HelpdeskException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>No usable GLPI token for the user – they have to connect (enter their password) again.</summary>
public sealed class HelpdeskNotConnectedException()
    : HelpdeskException("Połączenie z Helpdeskiem wygasło – podaj hasło, aby połączyć ponownie.");

public enum ConnectResult
{
    Connected,
    WrongPassword,
    Unavailable,
    NotConfigured,
}

/// <summary>
/// GLPI sign-in for intranet users. The API (v2) works only as a real user (OAuth2), so the password typed at the
/// intranet login is used once to obtain the user's GLPI tokens – and those are kept only after the second factor.
/// </summary>
public interface IHelpdeskConnector
{
    bool IsConfigured { get; }

    /// <summary>Called right after the password was verified – requests GLPI tokens in the background.</summary>
    void BeginConnect(DirectoryUser user, string password);

    /// <summary>Called when the login completes (second factor done) – stores the tokens requested by <see cref="BeginConnect"/>.</summary>
    void CompleteConnect(DirectoryUser user);

    Task<bool> IsConnectedAsync(string userSid, CancellationToken ct = default);

    /// <summary>Manual (re)connection from the Helpdesk page.</summary>
    Task<ConnectResult> ConnectAsync(DirectoryUser user, string password, CancellationToken ct = default);

    Task DisconnectAsync(string userSid, CancellationToken ct = default);
}

/// <summary>Tickets of the signed-in user – every call runs as that user in GLPI (their rights, their name).</summary>
public interface IHelpdeskService
{
    Task<IReadOnlyList<HelpdeskTicket>> GetMyTicketsAsync(string userSid, CancellationToken ct = default);

    Task<HelpdeskTicketDetails> GetTicketAsync(string userSid, int ticketId, CancellationToken ct = default);

    Task<IReadOnlyList<HelpdeskCategory>> GetCategoriesAsync(string userSid, TicketType type, CancellationToken ct = default);

    Task<IReadOnlyList<HelpdeskLocation>> GetLocationsAsync(string userSid, CancellationToken ct = default);

    /// <summary>Active GLPI users matching the text (name or login) – for choosing observers.</summary>
    /// <exception cref="HelpdeskException">The user's GLPI profile may not list users (403).</exception>
    Task<IReadOnlyList<HelpdeskUser>> SearchUsersAsync(string userSid, string text, CancellationToken ct = default);

    /// <summary>Creates the ticket, then adds the observers (a failed observer does not undo the ticket).</summary>
    Task<NewTicketResult> CreateTicketAsync(string userSid, NewTicketInput input, CancellationToken ct = default);

    Task AddObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default);

    Task RemoveObserverAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default);

    /// <summary>Adds a reply, a private note or a solution; attachments go to the ticket too. Text may be empty when files are attached.</summary>
    /// <returns>Names of files GLPI did not accept.</returns>
    Task<IReadOnlyList<string>> AddEntryAsync(string userSid, int ticketId, EntryKind kind, string? text,
        IReadOnlyList<HelpdeskAttachment> attachments, CancellationToken ct = default);

    /// <summary>The user's active GLPI profile – technicians get the ticket management tools.</summary>
    Task<HelpdeskAccess> GetAccessAsync(string userSid, CancellationToken ct = default);

    Task UpdateTicketAsync(string userSid, int ticketId, TicketChanges changes, CancellationToken ct = default);

    Task AddAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default);

    Task RemoveAssigneeAsync(string userSid, int ticketId, int glpiUserId, CancellationToken ct = default);

    /// <summary>Whether files can be attached (needs the GLPI legacy API – see <c>Glpi:Attachments</c>).</summary>
    bool CanAttachFiles { get; }

    /// <summary>Largest accepted file, in bytes.</summary>
    long MaxAttachmentBytes { get; }

    int MaxAttachments { get; }

    /// <summary>
    /// A document of the ticket or of one of its messages. Read with the user's own rights; when GLPI does not let the
    /// user download it directly, it is served only if it belongs to a message the user can see.
    /// </summary>
    Task<HelpdeskFile> DownloadDocumentAsync(string userSid, int ticketId, int documentId, CancellationToken ct = default);

    /// <summary>Accepts (closes the ticket) or refuses (reopens it, with a comment) the proposed solution.</summary>
    Task AnswerSolutionAsync(string userSid, int ticketId, int solutionId, bool accept, string? comment, CancellationToken ct = default);

    /// <summary>Link to the ticket in GLPI's own interface.</summary>
    string TicketUrl(int ticketId);
}
