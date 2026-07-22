using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SwissKnife.Core.Entities;

namespace SwissKnife.Core.Persistence;

public sealed class SwissKnifeDbContext(DbContextOptions<SwissKnifeDbContext> options) : DbContext(options)
{
    // SQLite não traduz ORDER BY/comparações sobre DateTimeOffset nativamente; convertendo
    // para um Int64 (ticks UTC) o valor permanece ordenável e comparável em todos os
    // providers suportados (SQLite/Postgres/SqlServer).
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<TenantLimit> TenantLimits => Set<TenantLimit>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceTag> ResourceTags => Set<ResourceTag>();
    public DbSet<ResourceComment> ResourceComments => Set<ResourceComment>();
    public DbSet<ResourceAttachment> ResourceAttachments => Set<ResourceAttachment>();
    public DbSet<ResourceRelationship> ResourceRelationships => Set<ResourceRelationship>();
    public DbSet<ResourceHistory> ResourceHistories => Set<ResourceHistory>();
    public DbSet<ResourceStateTransition> ResourceStateTransitions => Set<ResourceStateTransition>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<ResourceTemplate> ResourceTemplates => Set<ResourceTemplate>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<ResourceExternalKey> ResourceExternalKeys => Set<ResourceExternalKey>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportConflict> ImportConflicts => Set<ImportConflict>();

    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<ScheduledJobEntity> ScheduledJobs => Set<ScheduledJobEntity>();
    public DbSet<IdempotencyKeyEntity> IdempotencyKeys => Set<IdempotencyKeyEntity>();
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<SecretReferenceEntity> SecretReferences => Set<SecretReferenceEntity>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<TemporaryElevationEntity> TemporaryElevations => Set<TemporaryElevationEntity>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<FeatureFlagEntity> FeatureFlags => Set<FeatureFlagEntity>();
    public DbSet<DynamicConfigEntity> DynamicConfigs => Set<DynamicConfigEntity>();
    public DbSet<DynamicConfigHistoryEntry> DynamicConfigHistory => Set<DynamicConfigHistoryEntry>();

    public DbSet<TicketEntity> Tickets => Set<TicketEntity>();
    public DbSet<TicketWatcher> TicketWatchers => Set<TicketWatcher>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketRelationship> TicketRelationships => Set<TicketRelationship>();
    public DbSet<TicketSlaPolicy> TicketSlaPolicies => Set<TicketSlaPolicy>();
    public DbSet<TicketNumberSequence> TicketNumberSequences => Set<TicketNumberSequence>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<OrgUnit>(b =>
        {
            b.Property(x => x.Kind).HasConversion<string>();
            b.HasIndex(x => new { x.TenantId, x.Kind, x.Name });
        });

        modelBuilder.Entity<ApiKeyEntity>(b =>
        {
            b.HasIndex(x => x.KeyHash).IsUnique();
        });

        modelBuilder.Entity<Resource>(b =>
        {
            // Unicidade de nome por módulo/tenant é reforçada em ResourceRepository (FND-009),
            // não como índice filtrado no banco: a sintaxe de filtro parcial difere entre
            // SQLite/Postgres/SqlServer e um registro soft-deletado não deve travar o nome.
            b.HasIndex(x => new { x.TenantId, x.Module, x.Name });
            b.HasIndex(x => new { x.TenantId, x.Module, x.UpdatedAt, x.Id });
            b.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResourceTag>(b => b.HasIndex(x => new { x.ResourceId, x.Tag }).IsUnique());

        modelBuilder.Entity<ResourceRelationship>(b =>
        {
            b.Property(x => x.RelationType).HasConversion<string>();
            b.HasIndex(x => new { x.SourceResourceId, x.TargetResourceId, x.RelationType }).IsUnique();
        });

        modelBuilder.Entity<ResourceHistory>(b =>
        {
            b.Property(x => x.ChangeKind).HasConversion<string>();
            b.HasIndex(x => new { x.ResourceId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<ResourceAttachment>(b => b.Property(x => x.ScanStatus).HasConversion<string>());

        modelBuilder.Entity<ResourceExternalKey>(b =>
            b.HasIndex(x => new { x.Source, x.ExternalKey }).IsUnique());

        modelBuilder.Entity<ImportJob>(b => b.Property(x => x.Format).HasConversion<string>());
        modelBuilder.Entity<ImportJob>(b => b.Property(x => x.Status).HasConversion<string>());

        modelBuilder.Entity<JobEntity>(b => b.Property(x => x.Status).HasConversion<string>());

        modelBuilder.Entity<IdempotencyKeyEntity>(b => b.HasKey(x => x.Key));

        modelBuilder.Entity<OutboxMessageEntity>(b => b.HasIndex(x => x.ProcessedAt));

        modelBuilder.Entity<UserEntity>(b => b.HasIndex(x => x.Email).IsUnique());
        modelBuilder.Entity<RefreshTokenEntity>(b => b.HasIndex(x => x.TokenHash).IsUnique());
        modelBuilder.Entity<FeatureFlagEntity>(b => b.HasIndex(x => new { x.Key, x.TenantId, x.Environment }).IsUnique());
        modelBuilder.Entity<DynamicConfigEntity>(b => b.HasIndex(x => new { x.Key, x.TenantId }).IsUnique());
        modelBuilder.Entity<AuditLogEntry>(b => b.HasIndex(x => new { x.TenantId, x.OccurredAt }));

        modelBuilder.Entity<TicketEntity>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>();
            b.Property(x => x.Priority).HasConversion<string>();
            b.Property(x => x.Impact).HasConversion<string>();
            b.Property(x => x.Urgency).HasConversion<string>();
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.ResponseDueAt });
            b.HasIndex(x => new { x.TenantId, x.ResolutionDueAt });
            b.HasMany(x => x.Watchers).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TicketRelationship>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>();
            b.HasIndex(x => new { x.SourceTicketId, x.TargetTicketId, x.Type }).IsUnique();
        });
        modelBuilder.Entity<TicketSlaPolicy>(b =>
        {
            b.Property(x => x.Priority).HasConversion<string>();
            b.HasIndex(x => new { x.TenantId, x.Priority }).IsUnique();
        });
        modelBuilder.Entity<TicketNumberSequence>(b => b.HasKey(x => x.TenantId));
        modelBuilder.Entity<FindingEntity>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>();
            b.HasIndex(x => new { x.TenantId, x.Fingerprint }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Module, x.Status, x.Severity });
        });

        // FND-010/031: filtro global de soft-delete. Isolamento por tenant é aplicado
        // explicitamente nos repositórios (via ITenantContext), não como filtro global,
        // para permitir operações administrativas cross-tenant com escopo platform:admin.
        modelBuilder.Entity<Resource>().HasQueryFilter(x => !x.IsDeleted);
    }
}
