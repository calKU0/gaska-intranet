namespace Gaska.Intranet.Core.Organization;

public enum PrincipalKind
{
    User = 1,
    Group = 2,
}

/// <summary>
/// AD user or group – used in the admin panel when granting permissions.
/// </summary>
public sealed record DirectoryPrincipal(string Sid, string AccountName, string DisplayName, PrincipalKind Kind);
