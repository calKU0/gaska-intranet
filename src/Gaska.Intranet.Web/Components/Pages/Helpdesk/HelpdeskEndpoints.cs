using Gaska.Intranet.Core.Helpdesk;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Web.Modules;
using Gaska.Intranet.Web.Security;

namespace Gaska.Intranet.Web.Components.Pages.Helpdesk;

public static class HelpdeskEndpoints
{
    public static string DocumentUrl(int ticketId, int documentId, bool download = false) =>
        $"/helpdesk/{ticketId}/dokument/{documentId}" + (download ? "?pobierz=1" : string.Empty);

    // Raster images only are shown in the page; anything else (HTML, SVG, PDF…) is always a download, so a file from a
    // ticket can never run script on the intranet's origin.
    private static readonly Dictionary<string, string> InlineImages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    public static IEndpointRouteBuilder MapHelpdeskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Read with the user's own GLPI token – GLPI decides whether they may see the document.
        endpoints.MapGet("/helpdesk/{ticketId:int}/dokument/{id:int}", async (int ticketId, int id, HttpContext http, IHelpdeskService helpdesk, CancellationToken ct) =>
        {
            HelpdeskFile file;
            try
            {
                file = await helpdesk.DownloadDocumentAsync(http.User.GetSid(), ticketId, id, ct);
            }
            catch (HelpdeskNotConnectedException)
            {
                return Results.Redirect("/helpdesk");
            }
            catch (HelpdeskException)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=600";
            return http.Request.Query["pobierz"].Count == 0 && InlineImages.TryGetValue(Path.GetExtension(file.FileName), out var imageType)
                ? Results.File(file.Content, imageType)
                : Results.File(file.Content, "application/octet-stream", file.FileName);
        })
        .RequireAuthorization(ModulePolicy.Prefix + ModuleKeys.Helpdesk);

        return endpoints;
    }
}
