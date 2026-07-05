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

        // FND-010/031: filtro global de soft-delete. Isolamento por tenant é aplicado
        // explicitamente nos repositórios (via ITenantContext), não como filtro global,
        // para permitir operações administrativas cross-tenant com escopo platform:admin.
        modelBuilder.Entity<Resource>().HasQueryFilter(x => !x.IsDeleted);
    }
}
