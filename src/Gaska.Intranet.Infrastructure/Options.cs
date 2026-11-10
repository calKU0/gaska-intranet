using System.ComponentModel.DataAnnotations;

namespace Gaska.Intranet.Infrastructure;

public enum DirectoryProvider
{
    /// <summary>Prawdziwe Active Directory (produkcja).</summary>
    ActiveDirectory,

    /// <summary>In-memory test data – development only, on a machine outside the domain.</summary>
    Fake,
}

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    public DirectoryProvider Provider { get; set; } = DirectoryProvider.ActiveDirectory;

    /// <summary>Domain name, e.g. "example.local". Empty = the domain the server belongs to.</summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Read-only AD account – required when the application server is not joined to the domain.
    /// Empty = the IIS application pool account (server in the domain).
    /// </summary>
    public ServiceAccountOptions ServiceAccount { get; set; } = new();

    /// <summary>
    /// Container (DN) searched for employees, e.g. "OU=Pracownicy,DC=gaska,DC=local".
    /// Empty = the whole domain.
    /// </summary>
    public string? EmployeesSearchBase { get; set; }

    /// <summary>Additional LDAP filter for the employee list (combined with AND).</summary>
    public string? EmployeesExtraFilter { get; set; } = "(givenName=*)(sn=*)";

    /// <summary>
    /// Accounts left out of the address book – sAMAccountName patterns with "*" / "?" (e.g. "ERPSYNC", "svc-*").
    /// Administrators can additionally hide single accounts in the address book itself.
    /// </summary>
    public List<string> EmployeesExcludedAccounts { get; set; } = [];

    /// <summary>Containers whose accounts are left out, as a part of the DN (e.g. "OU=Konta serwisowe").</summary>
    public List<string> EmployeesExcludedContainers { get; set; } = [];

    /// <summary>
    /// Optional: only members of this AD group (name or DN, nested membership counts) are listed – the most reliable
    /// filter when the company keeps an "all employees" group.
    /// </summary>
    public string? EmployeesGroup { get; set; }

    /// <summary>
    /// AD groups whose members are application administrators: name (sAMAccountName), SID
    /// or the alias "DomainAdmins" (Domain Admins) / "Administrators" (built-in Administrators).
    /// </summary>
    public List<string> AdministratorGroups { get; set; } = [];

    [Range(1, 1440)]
    public int EmployeesCacheMinutes { get; set; } = 15;

    /// <summary>How often (minutes) to refresh the signed-in user's groups and account status.</summary>
    [Range(1, 1440)]
    public int SessionRefreshMinutes { get; set; } = 30;

    public AttributeMap Attributes { get; set; } = new();

    public sealed class ServiceAccountOptions
    {
        public string? UserName { get; set; }
        public string? Password { get; set; }
    }

    public sealed class AttributeMap
    {
        public string Department { get; set; } = "department";
        public string JobTitle { get; set; } = "title";
        public string MobilePhone { get; set; } = "mobile";
        public string Extension { get; set; } = "ipPhone";
        public string Email { get; set; } = "mail";
    }
}

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary><c>false</c> = no SMS is sent, the text only goes to the log (same as "NiewysylajSMS" in B2B).</summary>
    public bool Enabled { get; set; }

    /// <summary>SMS gateway web service URL (appsettings.Local.json) – required when <see cref="Enabled"/>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gateway response that means success.</summary>
    public string SuccessResponse { get; set; } = "Wysłano do centrali";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary><c>false</c> = no e-mail is sent, the content only goes to the log.</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;

    /// <summary>None, Auto, SslOnConnect, StartTls, StartTlsWhenAvailable.</summary>
    public string Security { get; set; } = "Auto";

    public string? UserName { get; set; }
    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Gąska Intranet";
}

public sealed class TwoFactorOptions
{
    public const string SectionName = "TwoFactor";

    [Range(4, 10)]
    public int CodeLength { get; set; } = 6;

    [Range(1, 60)]
    public int CodeLifetimeMinutes { get; set; } = 5;

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    [Range(0, 600)]
    public int ResendCooldownSeconds { get; set; } = 30;

    /// <summary>Max number of codes sent per user within 15 minutes.</summary>
    [Range(1, 50)]
    public int MaxCodesPer15Minutes { get; set; } = 5;

    /// <summary>Max failed attempts per user within 15 minutes (before the application locks logins).</summary>
    [Range(1, 50)]
    public int MaxPasswordFailuresPer15Minutes { get; set; } = 5;

    /// <summary>Max failed attempts from one IP address within 15 minutes (protection against password spraying).</summary>
    [Range(1, 500)]
    public int MaxPasswordFailuresPerIpPer15Minutes { get; set; } = 20;

    /// <summary>
    /// How many days to remember a trusted device (no second factor at login). Trust is bound to one network –
    /// a login from a new address always requires a code. 0 = disabled.
    /// </summary>
    [Range(0, 90)]
    public int TrustedDeviceDays { get; set; } = 5;

    /// <summary>
    /// Require an authenticator app (TOTP). The first login is confirmed with an SMS/e-mail code, after which
    /// the user must set up the app; from then on every login uses the app.
    /// </summary>
    public bool RequireAuthenticatorApp { get; set; } = true;

    /// <summary>Name shown in the authenticator app next to the account.</summary>
    [Required]
    public string AuthenticatorIssuer { get; set; } = "Gaska Intranet";

    /// <summary>
    /// Optional public HTTPS URL of a square PNG logo shown next to the account in apps that support it
    /// (FreeOTP, 2FAS, Aegis). Google and Microsoft Authenticator do not support custom logos.
    /// </summary>
    public string? AuthenticatorImageUrl { get; set; }
}

/// <summary>Network-dependent access rules ("Security" section).</summary>
public sealed class AccessPolicyOptions
{
    public const string SectionName = "Security";

    /// <summary>Admin modules are available only from the internal network.</summary>
    public bool AdminOnlyFromInternalNetwork { get; set; } = true;
}
