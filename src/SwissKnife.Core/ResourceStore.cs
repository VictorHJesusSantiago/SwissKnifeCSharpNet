using System.Text.Json;

namespace SwissKnife.Core;

public interface IResourceStore
{
    Task<IReadOnlyList<ResourceRecord>> ListAsync(string? module = null, string? tenant = null, CancellationToken cancellationToken = default);
    Task<ResourceRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ResourceRecord> CreateAsync(CreateResource request, CancellationToken cancellationToken = default);
    Task<ResourceRecord?> UpdateAsync(Guid id, CreateResource request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class JsonResourceStore(string path) : IResourceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<ResourceRecord>> ListAsync(
        string? module = null,
        string? tenant = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            return items
                .Where(x => module is null || x.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                .Where(x => tenant is null || x.Tenant.Equals(tenant, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAt)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ResourceRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken: cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task<ResourceRecord> CreateAsync(CreateResource request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var item = new ResourceRecord
            {
                Module = request.Module,
                Name = request.Name.Trim(),
                Tenant = request.Tenant.Trim(),
                Status = request.Status.Trim(),
                Data = request.Data is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(request.Data, StringComparer.OrdinalIgnoreCase)
            };
            items.Add(item);
            await WriteUnsafeAsync(items, cancellationToken);
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task<ResourceRecord?> UpdateAsync(Guid id, CreateResource request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var item = items.FirstOrDefault(x => x.Id == id);
            if (item is null) return null;
            item.Module = request.Module;
            item.Name = request.Name.Trim();
            item.Tenant = request.Tenant.Trim();
            item.Status = request.Status.Trim();
            item.Data = request.Data ?? [];
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteUnsafeAsync(items, cancellationToken);
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadUnsafeAsync(cancellationToken);
            var removed = items.RemoveAll(x => x.Id == id) > 0;
            if (removed) await WriteUnsafeAsync(items, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<ResourceRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ResourceRecord>>(stream, JsonDefaults.Options, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(List<ResourceRecord> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, JsonDefaults.Options, cancellationToken);
        File.Move(temporary, _path, true);
    }

    private static void Validate(CreateResource request)
    {
        if (!ModuleCatalog.Exists(request.Module))
            throw new ArgumentException($"Módulo desconhecido: {request.Module}.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Nome é obrigatório.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Tenant))
            throw new ArgumentException("Tenant é obrigatório.", nameof(request));
    }
}
