using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Web.Theme;
using MudBlazor;

namespace Gaska.Intranet.Web.Components.Pages.Helpdesk;

/// <summary>Polish labels and colours for GLPI values.</summary>
public static class HelpdeskUi
{
    public static string StatusLabel(TicketStatus status) => status switch
    {
        TicketStatus.New => "Nowe",
        TicketStatus.Assigned => "W realizacji",
        TicketStatus.Planned => "Zaplanowane",
        TicketStatus.Waiting => "Oczekujące",
        TicketStatus.Solved => "Rozwiązane",
        TicketStatus.Closed => "Zamknięte",
        _ => status.ToString(),
    };

    public static Color StatusColor(TicketStatus status) => status switch
    {
        TicketStatus.New => Color.Info,
        TicketStatus.Assigned or TicketStatus.Planned => Color.Primary,
        TicketStatus.Waiting => Color.Warning,
        TicketStatus.Solved => Color.Success,
        _ => Color.Default,
    };

    public static string TypeLabel(TicketType type) => type == TicketType.Request ? "Prośba" : "Problem";

    public static readonly (int Value, string Label)[] Urgencies =
    [
        (1, "Bardzo niska"), (2, "Niska"), (3, "Średnia"), (4, "Wysoka"), (5, "Bardzo wysoka"),
    ];

    public static string UrgencyLabel(int urgency) =>
        Urgencies.FirstOrDefault(u => u.Value == urgency).Label ?? "Średnia";

    public static string RoleLabel(TicketRole role) =>
        role.HasFlag(TicketRole.Requester) ? "Moje" : role.HasFlag(TicketRole.Observer) ? "Obserwuję" : string.Empty;

    public static string Date(DateTime? date) =>
        date is { } d ? d.ToString(d.Date == DateTime.Today ? "'dziś' HH:mm" : "d.MM.yyyy HH:mm", GaskaTheme.Polish) : "—";
}
