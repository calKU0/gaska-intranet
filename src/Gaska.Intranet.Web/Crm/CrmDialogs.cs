using MudBlazor;

namespace Gaska.Intranet.Web.Crm;

public static class CrmDialogs
{
    /// <summary>Standard CRM dialog: medium width, full screen on phones (CSS .gaska-dialog), closes on Esc.</summary>
    public static DialogOptions Options => new()
    {
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = true,
        CloseButton = true,
    };
}
