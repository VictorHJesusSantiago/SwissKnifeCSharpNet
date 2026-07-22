using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Operations;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Findings;

public sealed record FindingInput(
    string Module,
    string Code,
    string Severity,
    string Title,
    string? Description = null,
    Guid? ResourceId = null,
    object? Evidence = null,
    string? Owner = null,
    string? DeduplicationKey = null);

public sealed class FindingService(
    SwissKnifeDbContext db,
    TenantContextAccessor tenant,
    TimeProvider timeProvider)
{
    private Guid TenantId => tenant.Current.TenantId;

    public async Task<FindingEntity> UpsertAsync(FindingInput input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        var now = timeProvider.GetUtcNow();
        var fingerprint = Fingerprint(input);
        var existing = await db.Findings.FirstOrDefaultAsync(
            x => x.TenantId == TenantId && x.Fingerprint == fingerprint, cancellationToken);
        if (existing is not null)
        {
            existing.OccurrenceCount++;
            existing.LastSeenAt = now;
            existing.Severity = input.Severity.ToLowerInvariant();
            existing.Title = input.Title.Trim();
            existing.Description = input.Description?.Trim();
            existing.EvidenceJson = input.Evidence is null ? existing.EvidenceJson : JsonSerializer.Serialize(input.Evidence, JsonDefaults.Options);
            if (existing.Status == FindingStatus.Resolved)
            {
                existing.Status = FindingStatus.Open;
                existing.ResolvedAt = null;
                existing.ResolvedBy = null;
            }
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var finding = new FindingEntity
        {
            TenantId = TenantId,
            Module = input.Module.ToLowerInvariant(),
            Code = input.Code.ToUpperInvariant(),
            Fingerprint = fingerprint,
            Severity = input.Severity.ToLowerInvariant(),
            Title = input.Title.Trim(),
            Description = input.Description?.Trim(),
            ResourceId = input.ResourceId,
            EvidenceJson = input.Evidence is null ? null : JsonSerializer.Serialize(input.Evidence, JsonDefaults.Options),
            Owner = input.Owner?.Trim(),
            FirstSeenAt = now,
            LastSeenAt = now
        };
        db.Findings.Add(finding);
        await db.SaveChangesAsync(cancellationToken);
        return finding;
    }

    public Task<List<FindingEntity>> ListAsync(string? module, string? status, string? severity, CancellationToken cancellationToken = default)
    {
        var query = db.Findings.AsNoTracking().Where(x => x.TenantId == TenantId);
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(x => x.Module == module);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<FindingStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(x => x.Severity == severity);
        return query.OrderByDescending(x => x.LastSeenAt).Take(1000).ToListAsync(cancellationToken);
    }

    public async Task<FindingEntity> DecideAsync(Guid id, FindingStatus status, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        if (status is not (FindingStatus.RiskAccepted or FindingStatus.FalsePositive or FindingStatus.Acknowledged))
            throw new ArgumentException("Decisão deve ser Acknowledged, RiskAccepted ou FalsePositive.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10) throw new ArgumentException("Justificativa deve ter ao menos 10 caracteres.");
        if (expiresAt is not null && expiresAt <= timeProvider.GetUtcNow()) throw new ArgumentException("Expiração deve estar no futuro.");
        var finding = await GetRequiredAsync(id, cancellationToken);
        finding.Status = status;
        finding.DecisionReason = reason.Trim();
        finding.DecisionExpiresAt = expiresAt;
        await db.SaveChangesAsync(cancellationToken);
        return finding;
    }

    public async Task<FindingEntity> ResolveAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        var finding = await GetRequiredAsync(id, cancellationToken);
        finding.Status = FindingStatus.Resolved;
        finding.ResolvedAt = timeProvider.GetUtcNow();
        finding.ResolvedBy = actor;
        await db.SaveChangesAsync(cancellationToken);
        return finding;
    }

    public async Task<FindingEntity> LinkTicketAsync(Guid id, Guid ticketId, CancellationToken cancellationToken = default)
    {
        var ticketExists = await db.Tickets.AnyAsync(x => x.Id == ticketId && x.TenantId == TenantId, cancellationToken);
        if (!ticketExists) throw new KeyNotFoundException($"Ticket {ticketId} não encontrado.");
        var finding = await GetRequiredAsync(id, cancellationToken);
        finding.LinkedTicketId = ticketId;
        await db.SaveChangesAsync(cancellationToken);
        return finding;
    }

    private async Task<FindingEntity> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Findings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken)
        ?? throw new KeyNotFoundException($"Finding {id} não encontrado.");

    private static string Fingerprint(FindingInput input)
    {
        var source = $"{input.Module.Trim().ToLowerInvariant()}|{input.Code.Trim().ToUpperInvariant()}|{input.ResourceId}|{input.DeduplicationKey?.Trim().ToLowerInvariant()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static void Validate(FindingInput input)
    {
        if (!ModuleCatalog.Exists(input.Module)) throw new ArgumentException($"Módulo desconhecido: {input.Module}.");
        if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 100) throw new ArgumentException("Código obrigatório, com até 100 caracteres.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 500) throw new ArgumentException("Título obrigatório, com até 500 caracteres.");
        if (!Enum.TryParse<FindingSeverity>(input.Severity, true, out _)) throw new ArgumentException("Severidade inválida.");
    }
}
