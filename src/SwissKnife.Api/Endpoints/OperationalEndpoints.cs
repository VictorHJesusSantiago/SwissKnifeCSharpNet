using SwissKnife.Core.Operations;
using SwissKnife.Core.Repositories;
using System.Text.Json;
using SwissKnife.Core;

namespace SwissKnife.Api.Endpoints;

public sealed record CapacityAnalysisRequest(
    IReadOnlyList<CapacityPerson> People,
    IReadOnlyList<CapacityAllocation> Allocations);
public sealed record WebhookSignRequest(string Payload, string Secret, string? EventId = null);
public sealed record WebhookVerifyRequest(string Payload, string Secret, WebhookSignature Signature, int ToleranceSeconds = 300);
public sealed record GatewaySelectionRequest(GatewayRoute Route, string AffinityKey);
public sealed record LogBatchRequest(IReadOnlyList<StructuredLogInput> Entries);
public sealed record PersistedOperationRequest(string Operation, string Name, JsonElement Input);

public static class OperationalEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/operations").WithTags("Operações especializadas");

        operations.MapPost("/vpn/audit", (VpnProfileAuditRequest request) =>
            Results.Ok(OperationalAnalysis.AuditVpn(request)));

        operations.MapPost("/cloud/audit", (CloudAuditRequest request) =>
            Results.Ok(OperationalAnalysis.AuditCloud(request)));

        operations.MapPost("/gateway/validate", (GatewayValidationRequest request) =>
            Results.Ok(OperationalAnalysis.ValidateGateway(request)));
        operations.MapPost("/gateway/select-upstream", (GatewaySelectionRequest request) =>
            Results.Ok(OperationalAnalysis.SelectGatewayUpstream(request.Route, request.AffinityKey)));

        operations.MapPost("/webhooks/validate", (WebhookEndpointDefinition request) =>
        {
            var findings = OperationalAnalysis.ValidateWebhookEndpoint(request);
            return Results.Ok(new { valid = findings.Count == 0, findings });
        });
        operations.MapPost("/webhooks/sign", (WebhookSignRequest request) =>
            Results.Ok(OperationalAnalysis.SignWebhook(request.Payload, request.Secret, request.EventId)));
        operations.MapPost("/webhooks/verify", (WebhookVerifyRequest request) =>
            Results.Ok(new
            {
                valid = OperationalAnalysis.VerifyWebhook(
                    request.Payload,
                    request.Secret,
                    request.Signature,
                    TimeSpan.FromSeconds(Math.Clamp(request.ToleranceSeconds, 1, 3600)))
            }));

        operations.MapPost("/ephemeral-environments/plan", (EphemeralEnvironmentPlanRequest request) =>
            Results.Ok(OperationalAnalysis.PlanEphemeralEnvironment(request)));

        operations.MapPost("/capacity/analyze", (CapacityAnalysisRequest request) =>
            Results.Ok(OperationalAnalysis.AnalyzeCapacity(request.People, request.Allocations)));

        operations.MapPost("/on-call/schedule", (OnCallScheduleRequest request) =>
            Results.Ok(OperationalAnalysis.GenerateOnCallSchedule(request)));

        operations.MapPost("/self-service/evaluate", (ProvisioningPolicyRequest request) =>
            Results.Ok(OperationalAnalysis.EvaluateProvisioning(request)));

        operations.MapPost("/disaster-recovery/assess", (DisasterRecoveryAssessmentRequest request) =>
            Results.Ok(OperationalAnalysis.AssessDisasterRecovery(request)));

        operations.MapPost("/itam/financials", (AssetFinancialRequest request) =>
            Results.Ok(OperationalAnalysis.CalculateAssetFinancials(request)));

        operations.MapPost("/licenses/reconcile", (LicensePositionRequest request) =>
            Results.Ok(OperationalAnalysis.ReconcileLicense(request)));

        operations.MapPost("/logs/validate-batch", (LogBatchRequest request) =>
            Results.Ok(OperationalAnalysis.ValidateLogBatch(request.Entries)));

        operations.MapPost("/executions", async (
            PersistedOperationRequest request,
            ResourceRepository resources,
            CancellationToken cancellationToken) =>
        {
            var (module, result) = Execute(request.Operation, request.Input);
            var payload = JsonSerializer.Serialize(new
            {
                operation = request.Operation,
                input = request.Input,
                result,
                executedAt = DateTimeOffset.UtcNow
            }, JsonDefaults.Options);
            var resource = await resources.CreateAsync(
                new CreateResourceCommand(module, request.Name, "completed", payload, ["automated", "operational-execution"]),
                cancellationToken);
            return Results.Created($"/api/resources/{resource.Id}", new { resource, result });
        });
    }

    private static (string Module, object Result) Execute(string operation, JsonElement input) =>
        operation.ToLowerInvariant() switch
        {
            "vpn-audit" => ("vpn-profiles", OperationalAnalysis.AuditVpn(Required<VpnProfileAuditRequest>(input))),
            "cloud-audit" => ("multi-cloud", OperationalAnalysis.AuditCloud(Required<CloudAuditRequest>(input))),
            "gateway-validate" => ("api-gateway", OperationalAnalysis.ValidateGateway(Required<GatewayValidationRequest>(input))),
            "gateway-select" => SelectGateway(input),
            "webhook-validate" => ("webhooks", OperationalAnalysis.ValidateWebhookEndpoint(Required<WebhookEndpointDefinition>(input))),
            "ephemeral-plan" => ("ephemeral-environments", OperationalAnalysis.PlanEphemeralEnvironment(Required<EphemeralEnvironmentPlanRequest>(input))),
            "capacity-analyze" => AnalyzeCapacity(input),
            "on-call-schedule" => ("on-call", OperationalAnalysis.GenerateOnCallSchedule(Required<OnCallScheduleRequest>(input))),
            "self-service-evaluate" => ("self-service", OperationalAnalysis.EvaluateProvisioning(Required<ProvisioningPolicyRequest>(input))),
            "dr-assess" => ("disaster-recovery", OperationalAnalysis.AssessDisasterRecovery(Required<DisasterRecoveryAssessmentRequest>(input))),
            "itam-financials" => ("itam", OperationalAnalysis.CalculateAssetFinancials(Required<AssetFinancialRequest>(input))),
            "licenses-reconcile" => ("licenses", OperationalAnalysis.ReconcileLicense(Required<LicensePositionRequest>(input))),
            "logs-validate-batch" => ValidateLogs(input),
            _ => throw new ArgumentException($"Operação desconhecida: {operation}.", nameof(operation))
        };

    private static (string, object) SelectGateway(JsonElement input)
    {
        var request = Required<GatewaySelectionRequest>(input);
        return ("api-gateway", OperationalAnalysis.SelectGatewayUpstream(request.Route, request.AffinityKey));
    }

    private static (string, object) AnalyzeCapacity(JsonElement input)
    {
        var request = Required<CapacityAnalysisRequest>(input);
        return ("team-capacity", OperationalAnalysis.AnalyzeCapacity(request.People, request.Allocations));
    }

    private static (string, object) ValidateLogs(JsonElement input)
    {
        var request = Required<LogBatchRequest>(input);
        return ("logs", OperationalAnalysis.ValidateLogBatch(request.Entries));
    }

    private static T Required<T>(JsonElement input) =>
        input.Deserialize<T>(JsonDefaults.Options)
        ?? throw new ArgumentException($"Payload inválido para {typeof(T).Name}.");
}
