using SwissKnife.Core.Entities;
using SwissKnife.Core.Findings;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api.Endpoints;

public sealed record FindingDecisionRequest(FindingStatus Status, string Reason, DateTimeOffset? ExpiresAt);
public sealed record FindingTicketRequest(Guid TicketId);

public static class FindingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var findings = api.MapGroup("/findings").WithTags("Findings");
        findings.MapGet("/", async (string? module, string? status, string? severity, FindingService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(module, status, severity, ct)));
        findings.MapPost("/", async (FindingInput input, FindingService service, CancellationToken ct) =>
        {
            var finding = await service.UpsertAsync(input, ct);
            return Results.Created($"/api/findings/{finding.Id}", finding);
        });
        findings.MapPost("/{id:guid}/decision", async (Guid id, FindingDecisionRequest request, FindingService service, CancellationToken ct) =>
            Results.Ok(await service.DecideAsync(id, request.Status, request.Reason, request.ExpiresAt, ct)));
        findings.MapPost("/{id:guid}/resolve", async (Guid id, FindingService service, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(await service.ResolveAsync(id, tenant.ActorName ?? "system", ct)));
        findings.MapPost("/{id:guid}/ticket", async (Guid id, FindingTicketRequest request, FindingService service, CancellationToken ct) =>
            Results.Ok(await service.LinkTicketAsync(id, request.TicketId, ct)));
    }
}
