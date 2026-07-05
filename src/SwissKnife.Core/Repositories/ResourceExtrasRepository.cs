using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Security;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Repositories;

/// <summary>
/// FND-015/016/017/019/020: comentários, anexos, relacionamentos, campos customizados e
/// templates de recurso. Agrupados em um repositório complementar para não inchar
/// ResourceRepository, que já concentra o núcleo de CRUD/versionamento.
/// </summary>
public sealed class ResourceExtrasRepository(
    SwissKnifeDbContext db,
    TenantContextAccessor tenantAccessor,
    IAttachmentScanner scanner,
    string attachmentsRootDirectory)
{
    private Guid TenantId => tenantAccessor.Current.TenantId;

    public async Task<ResourceComment> AddCommentAsync(Guid resourceId, string body, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedByTenantAsync(resourceId, cancellationToken);
        var comment = new ResourceComment { ResourceId = resourceId, Body = body };
        db.ResourceComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);
        return comment;
    }

    public async Task<IReadOnlyList<ResourceComment>> ListCommentsAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        await db.ResourceComments.AsNoTracking()
            .Where(x => x.ResourceId == resourceId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<ResourceAttachment> AddAttachmentAsync(Guid resourceId, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedByTenantAsync(resourceId, cancellationToken);

        Directory.CreateDirectory(attachmentsRootDirectory);
        var storedName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var storagePath = Path.Combine(attachmentsRootDirectory, storedName);

        using var sha256 = SHA256.Create();
        long size;
        await using (var target = File.Create(storagePath))
        await using (var hashing = new CryptoStream(target, sha256, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashing, cancellationToken);
        }
        size = new FileInfo(storagePath).Length;
        var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

        var scanStatus = await scanner.ScanAsync(storagePath, cancellationToken);
        var attachment = new ResourceAttachment
        {
            ResourceId = resourceId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = size,
            Sha256Hash = hash,
            StoragePath = storagePath,
            ScanStatus = scanStatus,
            UploadedBy = tenantAccessor.Current.ActorName
        };
        db.ResourceAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return attachment;
    }

    public async Task<IReadOnlyList<ResourceAttachment>> ListAttachmentsAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        await db.ResourceAttachments.AsNoTracking()
            .Where(x => x.ResourceId == resourceId)
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);

    public async Task<ResourceRelationship> AddRelationshipAsync(Guid sourceId, Guid targetId, ResourceRelationType type, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedByTenantAsync(sourceId, cancellationToken);
        await EnsureOwnedByTenantAsync(targetId, cancellationToken);
        var relationship = new ResourceRelationship { SourceResourceId = sourceId, TargetResourceId = targetId, RelationType = type };
        db.ResourceRelationships.Add(relationship);
        await db.SaveChangesAsync(cancellationToken);
        return relationship;
    }

    public async Task<IReadOnlyList<ResourceRelationship>> ListRelationshipsAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        await db.ResourceRelationships.AsNoTracking()
            .Where(x => x.SourceResourceId == resourceId || x.TargetResourceId == resourceId)
            .ToListAsync(cancellationToken);

    public async Task<CustomFieldDefinition> DefineCustomFieldAsync(string module, string fieldName, string fieldType, bool required, string? defaultValue, CancellationToken cancellationToken = default)
    {
        var definition = new CustomFieldDefinition
        {
            TenantId = TenantId,
            Module = module,
            FieldName = fieldName,
            FieldType = fieldType,
            Required = required,
            DefaultValue = defaultValue
        };
        db.CustomFieldDefinitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);
        return definition;
    }

    public async Task<IReadOnlyList<CustomFieldDefinition>> ListCustomFieldsAsync(string module, CancellationToken cancellationToken = default) =>
        await db.CustomFieldDefinitions.AsNoTracking()
            .Where(x => x.TenantId == TenantId && x.Module == module)
            .ToListAsync(cancellationToken);

    public async Task SetCustomFieldValueAsync(Guid resourceId, Guid fieldDefinitionId, string? value, CancellationToken cancellationToken = default)
    {
        var existing = await db.CustomFieldValues.FirstOrDefaultAsync(
            x => x.ResourceId == resourceId && x.FieldDefinitionId == fieldDefinitionId, cancellationToken);
        if (existing is null)
            db.CustomFieldValues.Add(new CustomFieldValue { ResourceId = resourceId, FieldDefinitionId = fieldDefinitionId, Value = value });
        else
            existing.Value = value;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ResourceTemplate> CreateTemplateAsync(string module, string name, string payloadJsonTemplate, bool global, CancellationToken cancellationToken = default)
    {
        var template = new ResourceTemplate { TenantId = global ? null : TenantId, Module = module, Name = name, PayloadJsonTemplate = payloadJsonTemplate };
        db.ResourceTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task<IReadOnlyList<ResourceTemplate>> ListTemplatesAsync(string module, CancellationToken cancellationToken = default) =>
        await db.ResourceTemplates.AsNoTracking()
            .Where(x => x.Module == module && (x.TenantId == null || x.TenantId == TenantId))
            .ToListAsync(cancellationToken);

    private async Task EnsureOwnedByTenantAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var owned = await db.Resources.IgnoreQueryFilters().AnyAsync(x => x.Id == resourceId && x.TenantId == TenantId, cancellationToken);
        if (!owned) throw new KeyNotFoundException($"Recurso {resourceId} não encontrado neste tenant.");
    }
}
