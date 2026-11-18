using Gaska.Intranet.Core.Analytics;
using MudBlazor;

namespace Gaska.Intranet.Web.Components.Charts;

/// <summary>Icon names of the metric catalog → the application's icon set.</summary>
public static class MetricIcons
{
    public static string For(MetricDefinition? metric) => For(metric?.Icon);

    public static string For(string? name) => name switch
    {
        "money" => Icons.Material.Outlined.Payments,
        "receipt" => Icons.Material.Outlined.ReceiptLong,
        "cart" => Icons.Material.Outlined.ShoppingCart,
        "cartOff" => Icons.Material.Outlined.RemoveShoppingCart,
        "users" => Icons.Material.Outlined.Groups,
        "person" => Icons.Material.Outlined.Badge,
        "personAdd" => Icons.Material.Outlined.PersonAddAlt,
        "box" => Icons.Material.Outlined.Inventory2,
        "globe" => Icons.Material.Outlined.Public,
        "mail" => Icons.Material.Outlined.Mail,
        "mailIn" => Icons.Material.Outlined.MoveToInbox,
        "mailOut" => Icons.Material.Outlined.Outbox,
        "phone" => Icons.Material.Outlined.Phone,
        "meeting" => Icons.Material.Outlined.Handshake,
        "task" => Icons.Material.Outlined.TaskAlt,
        "topic" => Icons.Material.Outlined.Label,
        "clock" => Icons.Material.Outlined.Schedule,
        "complaint" => Icons.Material.Outlined.ReportProblem,
        "truck" => Icons.Material.Outlined.LocalShipping,
        "return" => Icons.Material.Outlined.AssignmentReturn,
        "list" => Icons.Material.Outlined.Checklist,
        "wallet" => Icons.Material.Outlined.AccountBalanceWallet,
        "alert" => Icons.Material.Outlined.NotificationImportant,
        "login" => Icons.Material.Outlined.Login,
        "search" => Icons.Material.Outlined.Search,
        "searchOff" => Icons.Material.Outlined.SearchOff,
        "eye" => Icons.Material.Outlined.Visibility,
        _ => Icons.Material.Outlined.BarChart,
    };
}
