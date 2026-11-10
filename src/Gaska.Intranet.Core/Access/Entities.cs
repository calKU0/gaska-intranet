using Gaska.Intranet.Core.Modules;
using Gaska.Intranet.Core.Organization;

namespace Gaska.Intranet.Core.Access;

/// <summary>Module access mode overridden by an administrator.</summary>
public class ModuleSetting
{
    public string ModuleKey { get; set; } = string.Empty;
    public ModuleAccessMode AccessMode { get; set; }

    /// <summary>
    /// The module works only for requests from the company network (<c>Security:InternalNetworks</c>) –
    /// applies to everyone, administrators included.
    /// </summary>
    public bool InternalNetworkOnly { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>Grants an AD user or group access to a module (applies to <see cref="ModuleAccessMode.Restricted"/>).</summary>
public class ModuleAccessRule
{
    public int Id { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public PrincipalKind PrincipalKind { get; set; }

    /// <summary>SID – permanent account/group identifier (does not change on rename).</summary>
    public string PrincipalSid { get; set; } = string.Empty;

    /// <summary>Display name captured when the permission was granted.</summary>
    public string PrincipalName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
