using Gaska.Intranet.Core.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Web.Security;

/// <summary>
/// Protects a page with a module permission: <c>@attribute [ModuleAuthorize(ModuleKeys.Employees)]</c>.
/// </summary>
public sealed class ModuleAuthorizeAttribute(string moduleKey) : AuthorizeAttribute(ModulePolicy.Prefix + moduleKey);

public static class ModulePolicy
{
    public const string Prefix = "Module:";
}

public sealed record ModuleAccessRequirement(string ModuleKey) : IAuthorizationRequirement;

internal sealed class ModuleAccessHandler(IModuleAccessService access) : AuthorizationHandler<ModuleAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ModuleAccessRequirement requirement)
    {
        if (await access.CanAccessAsync(context.User, requirement.ModuleKey))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Builds "Module:{key}" policies on the fly – no manual registration needed.</summary>
internal sealed class ModulePolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(ModulePolicy.Prefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        return new AuthorizationPolicyBuilder(AuthSchemes.Main)
            .RequireAuthenticatedUser()
            .AddRequirements(new ModuleAccessRequirement(policyName[ModulePolicy.Prefix.Length..]))
            .Build();
    }
}
