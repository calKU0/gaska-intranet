using Microsoft.JSInterop;

namespace Gaska.Intranet.Web.Components.Shared;

/// <summary>Scrolling helpers for forms (on a phone the save button is usually far from the problem).</summary>
public static class FormScroll
{
    /// <summary>Form submitted with invalid fields: scroll to the first field with a validation message.</summary>
    public static async Task RevealInvalidFieldAsync(this IJSRuntime js)
    {
        try
        {
            await js.InvokeVoidAsync("gaskaRevealInvalid");
        }
        catch (JSDisconnectedException)
        {
            // The tab was closed.
        }
    }
}
