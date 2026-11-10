namespace Gaska.Intranet.Web.Theme;

/// <summary>Dark (default) or light – the user's choice, kept in a cookie so the server renders the right colours
/// straight away (no flash of the other theme before Blazor starts).</summary>
public enum ThemeMode
{
    Dark,
    Light,
}

public static class ThemeCookies
{
    /// <summary>"light" or "dark" (written by js/boot.js when the user flips the switch).</summary>
    public const string Mode = "gaska-theme";

    /// <summary>Dark unless the user chose light.</summary>
    public static ThemeMode ReadMode(HttpContext? http) =>
        http?.Request.Cookies[Mode] == "light" ? ThemeMode.Light : ThemeMode.Dark;

    public static string ToCookieValue(ThemeMode mode) => mode == ThemeMode.Light ? "light" : "dark";
}
