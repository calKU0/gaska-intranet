namespace Gaska.Intranet.Web.Security;

public static class AuthSchemes
{
    /// <summary>Full session – after password and second factor.</summary>
    public const string Main = "Gaska";

    /// <summary>Short-lived cookie between the password step and the final sign-in.</summary>
    public const string Pending = "Gaska.Pending";
}

/// <summary>Account page URLs (in Polish, as seen by users).</summary>
public static class AccountRoutes
{
    public const string Login = "/konto/logowanie";
    public const string ChooseMethod = "/konto/weryfikacja/metoda";
    public const string Verify = "/konto/weryfikacja";
    public const string AuthenticatorCode = "/konto/weryfikacja/aplikacja";
    public const string AuthenticatorSetup = "/konto/aplikacja-uwierzytelniajaca";
    public const string Logout = "/konto/wyloguj";
    public const string AccessDenied = "/konto/brak-dostepu";
    public const string SessionPing = "/konto/sesja";
}
