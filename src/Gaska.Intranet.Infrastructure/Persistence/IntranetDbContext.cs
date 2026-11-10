using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Infrastructure.Helpdesk;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Gaska.Intranet.Infrastructure.Persistence;

/// <summary>
/// Application tables live in a separate "intranet" schema – they can sit in the XL database next to the CDN tables
/// without name clashes. ERP/CRM data will be read through separate (read-only) contexts.
/// </summary>
public class IntranetDbContext(DbContextOptions<IntranetDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "intranet";

    public DbSet<ModuleSetting> ModuleSettings => Set<ModuleSetting>();
    public DbSet<ModuleAccessRule> ModuleAccessRules => Set<ModuleAccessRule>();
    public DbSet<LoginChallenge> LoginChallenges => Set<LoginChallenge>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<UserAuthenticator> UserAuthenticators => Set<UserAuthenticator>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<GlpiConnection> GlpiConnections => Set<GlpiConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntranetDbContext).Assembly);
    }
}
