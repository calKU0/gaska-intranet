namespace Gaska.Intranet.Web.Security;

/// <summary>Trusted device cookie (the token only – no personal data).</summary>
public static class TrustedDeviceCookie
{
    // The __Host- prefix enforces: HTTPS only, this host only, path "/".
    public const string Name = "__Host-Gaska.Device";

    public static string? Read(HttpContext http) => http.Request.Cookies[Name];

    public static void Write(HttpContext http, string token, TimeSpan lifetime) =>
        http.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime),
            IsEssential = true,
        });

    public static void Delete(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { Secure = true, Path = "/", SameSite = SameSiteMode.Strict });
}
