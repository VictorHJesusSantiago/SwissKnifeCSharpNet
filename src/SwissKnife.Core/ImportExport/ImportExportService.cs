using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Repositories;
using SwissKnife.Core.Tenancy;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwissKnife.Core.ImportExport;

public sealed record ImportRow(string Name, string Status, string PayloadJson, string? ExternalKey);
public sealed record ImportPreviewItem(int RowNumber, ImportRow Row, IReadOnlyList<string> Errors);
public sealed record ImportPreview(int TotalRows, int ValidRows, int InvalidRows, IReadOnlyList<ImportPreviewItem> Items);

/// <summary>
/// FND-021/022/023: importação/exportação CSV/JSON/YAML com idempotência via chave externa
/// natural (Module+Tenant+ExternalKey) e relatório de conflitos por linha.
/// </summary>
public sealed class ImportExportService(SwissKnifeDbContext db, ResourceRepository resources, TenantContextAccessor tenantAccessor)
{
    private const string ExternalKeySource = "import";
    private Guid TenantId => tenantAccessor.Current.TenantId;

    public async Task<ImportPreview> PreviewAsync(string module, ImportFormat format, Stream content, CancellationToken cancellationToken = default)
    {
        if (!ModuleCatalog.Exists(module))
            throw new ArgumentException($"Módulo desconhecido: {module}.");
        using var reader = new StreamReader(content);
        var rows = Parse(format, await reader.ReadToEndAsync(cancellationToken));
        var items = rows.Select((row, index) =>
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(row.Name)) errors.Add("Nome é obrigatório.");
            errors.AddRange(Schema.ModuleSchemaRegistry.Validate(module, row.PayloadJson));
            return new ImportPreviewItem(index + 1, row, errors);
        }).ToArray();
        return new ImportPreview(items.Length, items.Count(x => x.Errors.Count == 0),
            items.Count(x => x.Errors.Count > 0), items);
    }

    public async Task<ImportJob> ImportAsync(string module, ImportFormat format, Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        if (!ModuleCatalog.Exists(module))
            throw new ArgumentException($"Módulo desconhecido: {module}.");
        var rows = Parse(format, text);

        var job = new ImportJob { TenantId = TenantId, Module = module, Format = format, SourceFileHash = hash, TotalRows = rows.Count };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            try
            {
                await ImportRowAsync(module, row, job.Id, rowNumber, cancellationToken);
                job.ProcessedRows++;
            }
            catch (Exception exception)
            {
                job.ConflictCount++;
                db.ImportConflicts.Add(new ImportConflict
                {
                    ImportJobId = job.Id,
                    RowNumber = rowNumber,
                    Reason = exception.Message,
                    RawData = JsonSerializer.Serialize(row)
                });
            }
        }

        job.Status = ImportJobStatus.Completed;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<(ImportJob Job, IReadOnlyList<ImportConflict> Conflicts)?> GetReportAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.ImportJobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId && x.TenantId == TenantId, cancellationToken);
        if (job is null) return null;
        var conflicts = await db.ImportConflicts.AsNoTracking()
            .Where(x => x.ImportJobId == jobId).OrderBy(x => x.RowNumber).ToListAsync(cancellationToken);
        return (job, conflicts);
    }

    private static List<ImportRow> Parse(ImportFormat format, string text) => format switch
    {
        ImportFormat.Csv => ParseCsv(text),
        ImportFormat.Json => ParseJson(text),
        ImportFormat.Yaml => ParseYaml(text),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private async Task ImportRowAsync(string module, ImportRow row, Guid jobId, int rowNumber, CancellationToken cancellationToken)
    {
        Resource? existing = null;
        if (!string.IsNullOrWhiteSpace(row.ExternalKey))
        {
            var externalKey = await db.ResourceExternalKeys
                .FirstOrDefaultAsync(x => x.Source == ExternalKeySource && x.ExternalKey == row.ExternalKey, cancellationToken);
            if (externalKey is not null)
                existing = await db.Resources.FirstOrDefaultAsync(x => x.Id == externalKey.ResourceId, cancellationToken);
        }

        if (existing is not null)
        {
            await resources.UpdateAsync(existing.Id, new UpdateResourceCommand(row.Name, row.Status, row.PayloadJson, existing.ConcurrencyStamp), cancellationToken);
            return;
        }

        var created = await resources.CreateAsync(new CreateResourceCommand(module, row.Name, row.Status, row.PayloadJson), cancellationToken);
        if (!string.IsNullOrWhiteSpace(row.ExternalKey))
        {
            db.ResourceExternalKeys.Add(new ResourceExternalKey { ResourceId = created.Id, ExternalKey = row.ExternalKey, Source = ExternalKeySource });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<ImportRow> ParseCsv(string text)
    {
        using var stringReader = new StringReader(text);
        using var csv = new CsvReader(stringReader, CultureInfo.InvariantCulture);
        var rows = new List<ImportRow>();
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            var name = csv.GetField("name") ?? throw new FormatException("Coluna 'name' ausente.");
            var status = csv.GetField("status") ?? "active";
            var externalKey = csv.TryGetField<string>("externalKey", out var key) ? key : null;
            var payload = new Dictionary<string, string>();
            foreach (var header in csv.HeaderRecord ?? [])
            {
                if (header is "name" or "status" or "externalKey") continue;
                payload[header] = csv.GetField(header) ?? "";
            }
            rows.Add(new ImportRow(name, status, JsonSerializer.Serialize(payload), externalKey));
        }
        return rows;
    }

    private static List<ImportRow> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        var rows = new List<ImportRow>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            var status = element.TryGetProperty("status", out var s) ? s.GetString() ?? "active" : "active";
            var externalKey = element.TryGetProperty("externalKey", out var k) ? k.GetString() : null;
            var payload = element.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
            rows.Add(new ImportRow(name, status, payload, externalKey));
        }
        return rows;
    }

    private static List<ImportRow> ParseYaml(string text)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var items = deserializer.Deserialize<List<Dictionary<string, object>>>(text) ?? [];
        return items.Select(item =>
        {
            var name = item["name"].ToString()!;
            var status = item.TryGetValue("status", out var s) ? s.ToString()! : "active";
            var externalKey = item.TryGetValue("externalKey", out var k) ? k.ToString() : null;
            var data = item.Where(kv => kv.Key is not ("name" or "status" or "externalKey")).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
            return new ImportRow(name, status, JsonSerializer.Serialize(data), externalKey);
        }).ToList();
    }

    public async Task<string> ExportAsync(string module, ImportFormat format, CancellationToken cancellationToken = default)
    {
        var items = await db.Resources.AsNoTracking()
            .Where(x => x.TenantId == TenantId && x.Module == module)
            .ToListAsync(cancellationToken);

        return format switch
        {
            ImportFormat.Json => JsonSerializer.Serialize(items.Select(x => new { x.Id, x.Name, x.Status, Data = JsonDocument.Parse(x.PayloadJson).RootElement }), new JsonSerializerOptions { WriteIndented = true }),
            ImportFormat.Yaml => new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
                .Serialize(items.Select(x => new { x.Id, x.Name, x.Status })),
            ImportFormat.Csv => ExportCsv(items),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static string ExportCsv(List<Resource> items)
    {
        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteField("id"); csv.WriteField("name"); csv.WriteField("status"); csv.WriteField("data");
        csv.NextRecord();
        foreach (var item in items)
        {
            csv.WriteField(item.Id.ToString());
            csv.WriteField(item.Name);
            csv.WriteField(item.Status);
            csv.WriteField(item.PayloadJson);
            csv.NextRecord();
        }
        return writer.ToString();
    }
}
