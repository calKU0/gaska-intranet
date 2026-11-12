namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Security headers for every response: Content-Security-Policy (blocks injected scripts
/// and resources from foreign domains), no framing (clickjacking), no caching of pages.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // MudBlazor sets inline styles, hence 'unsafe-inline' for styles only (scripts only from our own server).
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        // blob: – local files the user picked (the browser scales a business card photo down before uploading it).
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            // Internal site: keep it out of search engines, previews and AI training (robots.txt says the same).
            headers["X-Robots-Tag"] = "noindex, nofollow, noarchive, nosnippet, noimageindex, noai, noimageai";
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            // Pages (HTML, API responses) must not stay in browser/proxy caches – static files set their own headers.
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(http);
    }
}
