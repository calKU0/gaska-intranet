namespace Gaska.Intranet.Core.Auditing;

public static class AuditEvents
{
    public const string LoginPasswordFailed = "login.password_failed";
    public const string LoginPasswordOk = "login.password_ok";
    public const string LoginLockedOut = "login.locked_out";
    public const string CodeSent = "login.code_sent";
    public const string CodeSendFailed = "login.code_send_failed";
    public const string CodeInvalid = "login.code_invalid";
    public const string LoginSucceeded = "login.success";
    public const string LoginTrustedDevice = "login.trusted_device";
    public const string LoginAuthenticator = "login.authenticator";
    public const string AuthenticatorEnrolled = "authenticator.enrolled";
    public const string AuthenticatorInvalid = "authenticator.invalid_code";
    public const string AuthenticatorReset = "admin.authenticator_reset";
    public const string DeviceTrusted = "device.trusted";
    public const string DeviceForgotten = "device.forgotten";
    public const string DeviceRevoked = "admin.device_revoked";
    public const string RateLimited = "security.rate_limited";
    public const string BotDetected = "security.bot_detected";
    public const string Logout = "logout";
    public const string SessionRevoked = "session.revoked";
    public const string SessionExpired = "session.expired";
    public const string CrmContractorCreated = "crm.contractor_created";
    public const string CrmContractorUpdated = "crm.contractor_updated";
    public const string CrmContactSaved = "crm.contact_saved";
    public const string CrmTaskAdded = "crm.task_added";
    public const string RcpPunch = "rcp.punch";
    public const string HelpdeskTicketCreated = "helpdesk.ticket_created";
    public const string HelpdeskReply = "helpdesk.reply";
    public const string HelpdeskSolutionAnswered = "helpdesk.solution_answered";
    public const string HelpdeskSolutionAdded = "helpdesk.solution_added";
    public const string HelpdeskTicketChanged = "helpdesk.ticket_changed";
    // Hiding employees from the address book was removed – kept so that older log entries still have a label.
    public const string EmployeeHidden = "admin.employee_hidden";
    public const string EmployeeShown = "admin.employee_shown";
    public const string AccessModeChanged = "admin.access_mode_changed";
    public const string AccessNetworkChanged = "admin.access_network_changed";
    public const string AccessGranted = "admin.access_granted";
    public const string AccessRevoked = "admin.access_revoked";
}

/// <summary>Audit log entry (logins, permission changes).</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

public interface IAuditLog
{
    Task WriteAsync(string eventType, string? accountName, string? details = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 500, CancellationToken ct = default);
}
