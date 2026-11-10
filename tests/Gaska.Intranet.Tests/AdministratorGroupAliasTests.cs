using System.Runtime.Versioning;
using System.Security.Principal;
using Gaska.Intranet.Infrastructure.ActiveDirectory;

namespace Gaska.Intranet.Tests;

[SupportedOSPlatform("windows")]
public class AdministratorGroupAliasTests
{
    private static readonly SecurityIdentifier Domain = new("S-1-5-21-1111111111-2222222222-3333333333");

    [Fact]
    public void DomainAdmins_alias_maps_to_rid_512_of_user_domain() =>
        Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-512", ActiveDirectoryAccountService.ResolveAlias("DomainAdmins", Domain));

    [Fact]
    public void Administrators_alias_maps_to_builtin_group() =>
        Assert.Equal("S-1-5-32-544", ActiveDirectoryAccountService.ResolveAlias("administrators", Domain));

    [Fact]
    public void Sid_is_used_as_is() =>
        Assert.Equal("S-1-5-21-9-9-9-1234", ActiveDirectoryAccountService.ResolveAlias("S-1-5-21-9-9-9-1234", Domain));

    [Fact]
    public void Group_name_is_resolved_in_ad_not_here() =>
        Assert.Null(ActiveDirectoryAccountService.ResolveAlias("Intranet-Admins", Domain));
}
