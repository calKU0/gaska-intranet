using MudBlazor;

namespace Gaska.Intranet.Web.Links;

/// <summary>External links (HRM, helpdesk, shops…) from configuration section "QuickLinks".</summary>
public sealed class QuickLink
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Icon keyword: hr, helpdesk, web, b2b, shop, link.</summary>
    public string? Icon { get; set; }

    public string IconSvg => Icon?.ToLowerInvariant() switch
    {
        "hr" => Icons.Material.Outlined.Badge,
        "helpdesk" => Icons.Material.Outlined.SupportAgent,
        "web" => Icons.Material.Outlined.Language,
        "b2b" => Icons.Material.Outlined.Business,
        "shop" => Icons.Material.Outlined.ShoppingCart,
        _ => Icons.Material.Outlined.Link,
    };
}
