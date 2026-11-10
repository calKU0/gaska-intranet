namespace Gaska.Intranet.Infrastructure.Helpdesk;

/// <summary>
/// GLPI helpdesk (High-level API v2). ClientId / ClientSecret come from GLPI → Setup → OAuth Clients
/// (grant "Password", scopes api + user + email) and belong in appsettings.Local.json only.
/// </summary>
public sealed class GlpiOptions
{
    public const string SectionName = "Glpi";

    /// <summary>GLPI address, e.g. "https://helpdesk.example.com" (appsettings.Local.json).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API version in the URL – "v2" would follow GLPI upgrades, a pinned minor version stays predictable.</summary>
    public string ApiVersion { get; set; } = "v2.3";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Scope { get; set; } = "api user email";

    /// <summary>Response language of GLPI (status names etc.).</summary>
    public string Language { get; set; } = "pl_PL";

    /// <summary>Reads (lists, details).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Saves – GLPI runs ticket rules and sends notification e-mails before it answers.</summary>
    public int WriteTimeoutSeconds { get; set; } = 60;

    /// <summary>How many tickets the list shows (newest changes first).</summary>
    public int MaxTickets { get; set; } = 200;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public GlpiAttachmentOptions Attachments { get; set; } = new();
}

/// <summary>
/// File upload. API v2 cannot take files, so they go through GLPI's legacy REST API (apirest.php) as a service account:
/// GLPI → Setup → General → API: enable the REST API and "login with external token", add an API client (App-Token);
/// the service account (technician profile, rights to tickets and documents) gets an API token in its preferences.
/// The intranet checks with the user's own token that they can see the ticket before it uploads anything.
/// </summary>
public sealed class GlpiAttachmentOptions
{
    /// <summary>API token of the service account (its preferences → "Klucz API dostępu").</summary>
    public string? UserToken { get; set; }

    /// <summary>App-Token of the API client (optional in GLPI, required when the client has one).</summary>
    public string? AppToken { get; set; }

    /// <summary>
    /// GLPI profile to switch the service account to (optional). Without it the uploader picks a profile with the
    /// standard interface, preferring non-administrator ones – the account's default profile is often Self-Service.
    /// </summary>
    public int? ProfileId { get; set; }

    public int MaxFileMegabytes { get; set; } = 10;

    public int MaxFiles { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(UserToken);
}

/// <summary>GLPI tokens of one intranet user (encrypted in the database).</summary>
public sealed class GlpiConnection
{
    public string UserSid { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;

    /// <summary>The user's id in GLPI (to tell "my" tickets from observed ones).</summary>
    public int? GlpiUserId { get; set; }

    /// <summary>Access + refresh token, protected with ASP.NET Data Protection.</summary>
    public string ProtectedTokens { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresUtc { get; set; }
    public DateTime ConnectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
