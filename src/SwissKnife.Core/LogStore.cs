using System.Text.Json;

namespace SwissKnife.Core;

public sealed class LogStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(path);

    public async Task<LogEntry> AddAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Message)) throw new ArgumentException("Mensagem obrigatória.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry, JsonDefaults.CompactOptions) + Environment.NewLine, cancellationToken);
            return entry;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(
        string? level,
        string? source,
        string? text,
        string? tenant,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
            return lines
                .Select(x => JsonSerializer.Deserialize<LogEntry>(x, JsonDefaults.CompactOptions))
                .OfType<LogEntry>()
                .Where(x => level is null || x.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                .Where(x => source is null || x.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                .Where(x => tenant is null || x.Tenant.Equals(tenant, StringComparison.OrdinalIgnoreCase))
                .Where(x => text is null || x.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(take, 1, 1000))
                .ToArray();
        }
        finally { _gate.Release(); }
    }
}
