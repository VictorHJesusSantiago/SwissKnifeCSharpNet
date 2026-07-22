using System.Globalization;
using System.Text.Json;
using CsvHelper;
using SwissKnife.Core;
using YamlDotNet.Serialization;

namespace SwissKnife.Cli;

public enum OutputFormat { Table, Json, Yaml, Csv, Ndjson }

/// <summary>CLI-006: múltiplos formatos de saída para os mesmos dados retornados pela API.</summary>
public static class OutputFormatter
{
    public static void Print(JsonElement element, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                Console.WriteLine(JsonSerializer.Serialize(element, JsonDefaults.Options));
                break;
            case OutputFormat.Yaml:
                Console.WriteLine(new SerializerBuilder().Build().Serialize(ToObject(element)));
                break;
            case OutputFormat.Ndjson:
                foreach (var item in AsRows(element))
                    Console.WriteLine(JsonSerializer.Serialize(item, JsonDefaults.CompactOptions));
                break;
            case OutputFormat.Csv:
                PrintCsv(AsRows(element));
                break;
            case OutputFormat.Table:
            default:
                PrintTable(AsRows(element));
                break;
        }
    }

    private static IReadOnlyList<JsonElement> AsRows(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return [.. element.EnumerateArray()];
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return [.. items.EnumerateArray()];
        return [element];
    }

    private static void PrintTable(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) { Console.WriteLine("(vazio)"); return; }
        var columns = rows[0].ValueKind == JsonValueKind.Object
            ? rows[0].EnumerateObject().Select(p => p.Name).ToArray()
            : ["value"];

        var widths = columns.Select(c => c.Length).ToArray();
        var cellRows = rows.Select(row => columns.Select(c => CellText(row, c)).ToArray()).ToArray();
        foreach (var cells in cellRows)
            for (var i = 0; i < cells.Length; i++)
                widths[i] = Math.Max(widths[i], Math.Min(cells[i].Length, 60));

        Console.WriteLine(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var cells in cellRows)
            Console.WriteLine(string.Join("  ", cells.Select((cell, i) => Truncate(cell, 60).PadRight(widths[i]))));
    }

    private static string CellText(JsonElement row, string column)
    {
        if (row.ValueKind != JsonValueKind.Object) return row.ToString();
        return row.TryGetProperty(column, out var value) ? value.ToString() : "";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static void PrintCsv(IReadOnlyList<JsonElement> rows)
    {
        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        if (rows.Count > 0 && rows[0].ValueKind == JsonValueKind.Object)
        {
            var columns = rows[0].EnumerateObject().Select(p => p.Name).ToArray();
            foreach (var c in columns) csv.WriteField(c);
            csv.NextRecord();
            foreach (var row in rows)
            {
                foreach (var c in columns) csv.WriteField(CellText(row, c));
                csv.NextRecord();
            }
        }
        Console.WriteLine(writer.ToString());
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
