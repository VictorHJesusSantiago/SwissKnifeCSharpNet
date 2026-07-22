using System.Text.Json;
using SwissKnife.Core;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Repositories;
using SwissKnife.Core.Schema;
using SwissKnife.Core.Search;
using SwissKnife.Core.Security;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api.Endpoints;

public sealed record ResourceUpsertRequest(
    string Module,
    string Name,
    string Status = "active",
    Dictionary<string, object?>? Data = null,
    string[]? Tags = null,
    Guid? OwnerUserId = null,
    string? CostCenter = null);

public static class ResourceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/modules/{module}/schema", (string module) =>
            Results.Text(ModuleSchemaRegistry.GetSchemaText(module), "application/schema+json"));

        api.MapGet("/resources", async (string? module, string? status, string? tag, string? text,
            Guid? ownerUserId, bool? includeDeleted, string? cursor, int? pageSize,
            ResourceRepository repository, PayloadProtector protector, ITenantContext tenant, CancellationToken ct) =>
        {
            // Parâmetros de query vazios ("?status=") são tratados como ausentes, não como
            // filtro literal por string vazia — evita zerar listagens por engano de cliente.
            var filter = new ResourceFilter(
                NullIfEmpty(module), NullIfEmpty(status), NullIfEmpty(tag), NullIfEmpty(text),
                ownerUserId, includeDeleted ?? false);
            var page = await repository.ListAsync(filter, NullIfEmpty(cursor), pageSize ?? 25, ct);
            var items = page.Items.Select(x => ToResponse(x, protector, tenant));
            return Results.Ok(new { items, page.NextCursor, page.HasMore });
        });

        api.MapGet("/resources/trash", async (ResourceRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListTrashAsync(ct)));

        api.MapGet("/resources/{id:guid}", async (Guid id, ResourceRepository repository, PayloadProtector protector, ITenantContext tenant, HttpContext http, CancellationToken ct) =>
        {
            var item = await repository.GetAsync(id, ct);
            if (item is null) return Results.NotFound();
            var etag = $"\"{item.ConcurrencyStamp}\"";
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.Any(x => x == "*" || x == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(ToResponse(item, protector, tenant));
        });

        api.MapPost("/resources", async (ResourceUpsertRequest request, HttpContext http, ResourceRepository repository, CancellationToken ct) =>
        {
            var payload = JsonSerializer.Serialize(request.Data ?? new Dictionary<string, object?>());
            var created = await repository.CreateAsync(new CreateResourceCommand(
                request.Module, request.Name, request.Status, payload, request.Tags, request.OwnerUserId, CostCenter: request.CostCenter), ct);
            http.Response.Headers.ETag = $"\"{created.ConcurrencyStamp}\"";
            return Results.Created($"/api/resources/{created.Id}", created);
        });

        api.MapPut("/resources/{id:guid}", async (Guid id, ResourceUpsertRequest request, HttpContext http, ResourceRepository repository, CancellationToken ct) =>
        {
            var ifMatch = http.Request.Headers.IfMatch.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(ifMatch))
                return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                    title: "Pré-condição obrigatória", detail: "Informe o ETag atual no header If-Match.");
            var expectedStamp = ifMatch.Trim().Trim('"');
            var payload = JsonSerializer.Serialize(request.Data ?? new Dictionary<string, object?>());
            var updated = await repository.UpdateAsync(id, new UpdateResourceCommand(request.Name, request.Status, payload, expectedStamp, request.Tags, request.OwnerUserId, request.CostCenter), ct);
            http.Response.Headers.ETag = $"\"{updated.ConcurrencyStamp}\"";
            return Results.Ok(updated);
        });

        api.MapDelete("/resources/{id:guid}", async (Guid id, ResourceRepository repository, CancellationToken ct) =>
            await repository.SoftDeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/resources/{id:guid}/restore", async (Guid id, ResourceRepository repository, CancellationToken ct) =>
            await repository.RestoreAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

        api.MapGet("/resources/{id:guid}/history", async (Guid id, ResourceRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetHistoryAsync(id, ct)));

        api.MapGet("/resources/{id:guid}/diff", async (Guid id, int from, int to, ResourceRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.DiffVersionsAsync(id, from, to, ct)));

        api.MapPost("/resources/{id:guid}/versions/{version:int}/restore", async (Guid id, int version, ResourceRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.RestoreVersionAsync(id, version, ct)));

        api.MapGet("/resources/{id:guid}/comments", async (Guid id, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Ok(await extras.ListCommentsAsync(id, ct)));
        api.MapPost("/resources/{id:guid}/comments", async (Guid id, CommentRequest request, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Created($"/api/resources/{id}/comments", await extras.AddCommentAsync(id, request.Body, ct)));

        api.MapGet("/resources/{id:guid}/attachments", async (Guid id, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Ok(await extras.ListAttachmentsAsync(id, ct)));
        api.MapPost("/resources/{id:guid}/attachments", async (Guid id, HttpRequest http, ResourceExtrasRepository extras, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Envie multipart/form-data com o campo 'file'." });
            var form = await http.ReadFormAsync(ct);
            var file = form.Files["file"] ?? throw new ArgumentException("Campo 'file' ausente.");
            await using var stream = file.OpenReadStream();
            var attachment = await extras.AddAttachmentAsync(id, file.FileName, file.ContentType, stream, ct);
            return Results.Created($"/api/resources/{id}/attachments/{attachment.Id}", attachment);
        });

        api.MapGet("/resources/{id:guid}/relationships", async (Guid id, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Ok(await extras.ListRelationshipsAsync(id, ct)));
        api.MapPost("/resources/{id:guid}/relationships", async (Guid id, RelationshipRequest request, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Created($"/api/resources/{id}/relationships", await extras.AddRelationshipAsync(id, request.TargetId, request.Type, ct)));

        api.MapPost("/modules/{module}/custom-fields", async (string module, CustomFieldRequest request, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Created($"/api/modules/{module}/custom-fields", await extras.DefineCustomFieldAsync(module, request.FieldName, request.FieldType, request.Required, request.DefaultValue, ct)));
        api.MapGet("/modules/{module}/custom-fields", async (string module, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Ok(await extras.ListCustomFieldsAsync(module, ct)));

        api.MapPost("/modules/{module}/templates", async (string module, TemplateRequest request, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Created($"/api/modules/{module}/templates", await extras.CreateTemplateAsync(module, request.Name, request.PayloadJsonTemplate, request.Global, ct)));
        api.MapGet("/modules/{module}/templates", async (string module, ResourceExtrasRepository extras, CancellationToken ct) =>
            Results.Ok(await extras.ListTemplatesAsync(module, ct)));

        api.MapPost("/search/text", async (SearchRequest request, SearchService search, CancellationToken ct) =>
            Results.Ok(await search.SearchAsync(request.Text, request.Take ?? 50, ct)));
        api.MapPost("/search/saved", async (SavedSearchRequest request, SearchService search, CancellationToken ct) =>
            Results.Created("/api/search/saved", await search.SaveSearchAsync(request.Name, request.FilterJson, request.Favorite, ct)));
        api.MapGet("/search/saved", async (SearchService search, CancellationToken ct) =>
            Results.Ok(await search.ListSavedSearchesAsync(ct)));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static object ToResponse(Resource resource, PayloadProtector protector, ITenantContext tenant) => new
    {
        resource.Id,
        resource.Module,
        resource.Name,
        resource.Status,
        Data = JsonDocument.Parse(protector.MaskForResponse(resource.Module, resource.PayloadJson, tenant.HasScope("data:reveal"))).RootElement,
        resource.OwnerUserId,
        resource.CostCenter,
        resource.CreatedAt,
        resource.UpdatedAt,
        ETag = resource.ConcurrencyStamp,
        Tags = resource.Tags.Select(t => t.Tag)
    };
}

public sealed record CommentRequest(string Body);
public sealed record RelationshipRequest(Guid TargetId, ResourceRelationType Type);
public sealed record CustomFieldRequest(string FieldName, string FieldType, bool Required, string? DefaultValue);
public sealed record TemplateRequest(string Name, string PayloadJsonTemplate, bool Global);
public sealed record SearchRequest(string Text, int? Take);
public sealed record SavedSearchRequest(string Name, string FilterJson, bool Favorite);
