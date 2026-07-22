using System.Text.Json;
using SwissKnife.Core.Entities;
using SwissKnife.Core.ImportExport;
using SwissKnife.Core.Repositories;

namespace SwissKnife.Api.Endpoints;

public sealed record BatchCreateItem(string Module, string Name, string Status = "active",
    Dictionary<string, object?>? Data = null, string[]? Tags = null);
public sealed record BatchDeleteItem(Guid Id);
public sealed record BatchItemResult(int Index, Guid? Id, bool Success, string? Error);

/// <summary>FND-021/022/023/027: intercâmbio e mutações em lote com resultado por item.</summary>
public static class DataExchangeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/imports/preview", async (string module, ImportFormat format, HttpRequest request,
            ImportExportService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(module, format, request.Body, ct)));

        api.MapPost("/imports", async (string module, ImportFormat format, HttpRequest request,
            ImportExportService service, CancellationToken ct) =>
        {
            var job = await service.ImportAsync(module, format, request.Body, ct);
            return Results.Created($"/api/imports/{job.Id}", job);
        });

        api.MapGet("/imports/{id:guid}", async (Guid id, ImportExportService service, CancellationToken ct) =>
        {
            var report = await service.GetReportAsync(id, ct);
            return report is null
                ? Results.NotFound()
                : Results.Ok(new { report.Value.Job, report.Value.Conflicts });
        });

        api.MapGet("/exports", async (string module, ImportFormat format, ImportExportService service,
            CancellationToken ct) =>
        {
            var content = await service.ExportAsync(module, format, ct);
            var mediaType = format switch
            {
                ImportFormat.Csv => "text/csv",
                ImportFormat.Yaml => "application/yaml",
                _ => "application/json"
            };
            var extension = format.ToString().ToLowerInvariant();
            return Results.Text(content, mediaType, contentEncoding: System.Text.Encoding.UTF8,
                statusCode: StatusCodes.Status200OK);
        });

        api.MapPost("/resources/batch", async (IReadOnlyList<BatchCreateItem> items,
            ResourceRepository repository, CancellationToken ct) =>
        {
            if (items.Count is 0 or > 200)
                return Results.BadRequest(new { error = "O lote deve conter entre 1 e 200 itens." });
            var results = new List<BatchItemResult>(items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                try
                {
                    var created = await repository.CreateAsync(new CreateResourceCommand(item.Module,
                        item.Name, item.Status, JsonSerializer.Serialize(item.Data ?? []), item.Tags), ct);
                    results.Add(new(index, created.Id, true, null));
                }
                catch (Exception exception) when (exception is ArgumentException or ResourceValidationException
                                                   or DuplicateResourceNameException)
                {
                    results.Add(new(index, null, false, exception.Message));
                }
            }
            return Results.Ok(new
            {
                total = results.Count,
                succeeded = results.Count(x => x.Success),
                failed = results.Count(x => !x.Success),
                items = results
            });
        });

        api.MapPost("/resources/batch-delete", async (IReadOnlyList<BatchDeleteItem> items,
            ResourceRepository repository, CancellationToken ct) =>
        {
            if (items.Count is 0 or > 200)
                return Results.BadRequest(new { error = "O lote deve conter entre 1 e 200 itens." });
            var results = new List<BatchItemResult>(items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                var deleted = await repository.SoftDeleteAsync(items[index].Id, ct);
                results.Add(new(index, items[index].Id, deleted, deleted ? null : "Recurso não encontrado."));
            }
            return Results.Ok(new
            {
                total = results.Count,
                succeeded = results.Count(x => x.Success),
                failed = results.Count(x => !x.Success),
                items = results
            });
        });
    }
}
