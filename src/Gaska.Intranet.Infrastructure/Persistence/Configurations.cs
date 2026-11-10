using Gaska.Intranet.Core.Access;
using Gaska.Intranet.Core.Auditing;
using Gaska.Intranet.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gaska.Intranet.Infrastructure.Persistence;

internal sealed class ModuleSettingConfiguration : IEntityTypeConfiguration<ModuleSetting>
{
    public void Configure(EntityTypeBuilder<ModuleSetting> b)
    {
        b.HasKey(x => x.ModuleKey);
        b.Property(x => x.ModuleKey).HasMaxLength(64);
        b.Property(x => x.AccessMode).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.UpdatedBy).HasMaxLength(128);
    }
}

internal sealed class ModuleAccessRuleConfiguration : IEntityTypeConfiguration<ModuleAccessRule>
{
    public void Configure(EntityTypeBuilder<ModuleAccessRule> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.PrincipalKind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PrincipalSid).HasMaxLength(184).IsRequired();
        b.Property(x => x.PrincipalName).HasMaxLength(256).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(128);
        b.HasIndex(x => new { x.ModuleKey, x.PrincipalSid }).IsUnique();
    }
}

internal sealed class LoginChallengeConfiguration : IEntityTypeConfiguration<LoginChallenge>
{
    public void Configure(EntityTypeBuilder<LoginChallenge> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountName).HasMaxLength(128).IsRequired();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => new { x.AccountName, x.CreatedAtUtc });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(64).IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(128);
        b.Property(x => x.Details).HasMaxLength(1024);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.HasIndex(x => x.TimestampUtc);
        b.HasIndex(x => x.AccountName);
    }
}

internal sealed class TrustedDeviceConfiguration : IEntityTypeConfiguration<TrustedDevice>
{
    public void Configure(EntityTypeBuilder<TrustedDevice> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountName).HasMaxLength(128).IsRequired();
        b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        b.Property(x => x.IpAddress).HasMaxLength(64).IsRequired();
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.Property(x => x.RevokedBy).HasMaxLength(128);
        b.HasIndex(x => x.AccountName);
        b.HasIndex(x => x.ExpiresAtUtc);
    }
}

internal sealed class GlpiConnectionConfiguration : IEntityTypeConfiguration<Gaska.Intranet.Infrastructure.Helpdesk.GlpiConnection>
{
    public void Configure(EntityTypeBuilder<Gaska.Intranet.Infrastructure.Helpdesk.GlpiConnection> b)
    {
        b.HasKey(x => x.UserSid);
        b.Property(x => x.UserSid).HasMaxLength(184);
        b.Property(x => x.AccountName).HasMaxLength(128).IsRequired();
        b.Property(x => x.ProtectedTokens).HasMaxLength(8000).IsRequired();
    }
}

internal sealed class UserAuthenticatorConfiguration : IEntityTypeConfiguration<UserAuthenticator>
{
    public void Configure(EntityTypeBuilder<UserAuthenticator> b)
    {
        b.HasKey(x => x.UserSid);
        b.Property(x => x.UserSid).HasMaxLength(184);
        b.Property(x => x.AccountName).HasMaxLength(128).IsRequired();
        b.Property(x => x.ProtectedSecret).HasMaxLength(1024).IsRequired();
        b.HasIndex(x => x.AccountName);
    }
}
