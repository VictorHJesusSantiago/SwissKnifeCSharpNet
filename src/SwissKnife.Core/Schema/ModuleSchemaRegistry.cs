using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace SwissKnife.Core.Schema;

/// <summary>
/// FND-001/002/003: cada módulo tem um JSON Schema versionado que valida o payload
/// semi-tipado do recurso. Módulos sem schema próprio caem no schema permissivo padrão
/// (qualquer objeto JSON), permitindo migração incremental módulo a módulo.
/// </summary>
public static class ModuleSchemaRegistry
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TextCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSchema Permissive = new JsonSchemaBuilder().Type(SchemaValueType.Object);

    public static JsonSchema GetSchema(string moduleId)
    {
        return Cache.GetOrAdd(moduleId, LoadSchema);
    }

    public static string GetSchemaText(string moduleId)
    {
        if (!ModuleCatalog.Exists(moduleId))
            throw new KeyNotFoundException($"Módulo desconhecido: {moduleId}.");
        return TextCache.GetOrAdd(moduleId, LoadSchemaText);
    }

    private static JsonSchema LoadSchema(string moduleId) => JsonSchema.FromText(LoadSchemaText(moduleId));

    private static string LoadSchemaText(string moduleId)
    {
        var assembly = typeof(ModuleSchemaRegistry).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith($"ModuleSchemas.{moduleId}.schema.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""";

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> Validate(string moduleId, string payloadJson)
    {
        var schema = GetSchema(moduleId);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException exception)
        {
            return [$"Payload não é um JSON válido: {exception.Message}"];
        }

        using (document)
        {
            var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (results.IsValid) return [];

            return (results.Details ?? [])
                .Where(x => !x.IsValid && x.Errors is { Count: > 0 })
                .SelectMany(x => x.Errors!.Values.Select(message => $"{x.InstanceLocation}: {message}"))
                .Distinct()
                .ToArray();
        }
    }
}
