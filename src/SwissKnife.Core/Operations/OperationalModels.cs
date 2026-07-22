namespace SwissKnife.Core.Operations;

public enum FindingSeverity { Info, Low, Medium, High, Critical }
public sealed record OperationalFinding(string Code, FindingSeverity Severity, string Message, string? Resource = null, string? Recommendation = null);

public sealed record VpnProfileAuditRequest(
    string Name,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> DnsServers,
    bool FullTunnel,
    bool KillSwitch,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);
public sealed record VpnProfileAuditResult(bool Compliant, int RiskScore, IReadOnlyList<OperationalFinding> Findings);

public sealed record CloudResourceInput(
    string Provider,
    string Id,
    string Name,
    string Type,
    string Region,
    string State,
    string? Owner,
    decimal MonthlyCost,
    double UtilizationPercent,
    bool PubliclyExposed,
    IReadOnlyDictionary<string, string> Tags);
public sealed record CloudAuditRequest(
    IReadOnlyList<CloudResourceInput> Resources,
    IReadOnlyList<string> RequiredTags,
    decimal MonthlyBudget);
public sealed record CloudAuditResult(
    decimal TotalMonthlyCost,
    decimal PotentialSavings,
    bool BudgetExceeded,
    IReadOnlyDictionary<string, decimal> CostByProvider,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record GatewayUpstream(string Url, int Weight = 100, bool Healthy = true);
public sealed record GatewayRoute(
    string Id,
    string Path,
    IReadOnlyList<string> Methods,
    IReadOnlyList<GatewayUpstream> Upstreams,
    int TimeoutSeconds = 30,
    int Retries = 2,
    string Authentication = "jwt",
    long MaxBodyBytes = 1_048_576);
public sealed record GatewayValidationRequest(IReadOnlyList<GatewayRoute> Routes);
public sealed record GatewayValidationResult(bool Valid, IReadOnlyList<OperationalFinding> Findings);
public sealed record GatewaySelectionResult(string RouteId, string UpstreamUrl);

public sealed record WebhookEndpointDefinition(
    string Url,
    IReadOnlyList<string> Events,
    string Secret,
    bool AllowPrivateNetwork = false);
public sealed record WebhookSignature(string EventId, long Timestamp, string Algorithm, string Value);

public sealed record EphemeralEnvironmentPlanRequest(
    string Template,
    string Environment,
    int TtlHours,
    decimal HourlyCost,
    decimal RemainingBudget,
    int ActiveEnvironments,
    int EnvironmentQuota,
    bool UsesProductionData,
    bool DataIsMasked);
public sealed record EphemeralEnvironmentPlan(
    bool Approved,
    DateTimeOffset ExpiresAt,
    decimal EstimatedCost,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record CapacityPerson(
    string Id,
    string Name,
    decimal WeeklyHours,
    decimal AbsenceHours,
    decimal SupportReservePercent,
    IReadOnlyList<string> Skills);
public sealed record CapacityAllocation(string PersonId, string WorkItem, decimal Hours, string RequiredSkill);
public sealed record PersonCapacityResult(
    string PersonId,
    string Name,
    decimal NetCapacityHours,
    decimal AllocatedHours,
    decimal UtilizationPercent,
    IReadOnlyList<string> MissingSkills);
public sealed record CapacityPlanResult(
    decimal TotalCapacityHours,
    decimal TotalAllocatedHours,
    IReadOnlyList<PersonCapacityResult> People,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record OnCallParticipant(string Id, string Name, string TimeZoneId, bool Available = true);
public sealed record OnCallScheduleRequest(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int ShiftHours,
    IReadOnlyList<OnCallParticipant> Participants);
public sealed record OnCallShift(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string ParticipantId, string ParticipantName);
public sealed record OnCallScheduleResult(IReadOnlyList<OnCallShift> Shifts, IReadOnlyList<OperationalFinding> Findings);

public sealed record ProvisioningPolicyRequest(
    string Product,
    string Environment,
    decimal EstimatedMonthlyCost,
    decimal RemainingBudget,
    int RiskScore,
    bool Eligible,
    bool HasRequiredParameters,
    bool DryRun);
public sealed record ProvisioningDecision(
    bool CanProceed,
    bool RequiresApproval,
    IReadOnlyList<string> PlannedSteps,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record DisasterRecoveryAssessmentRequest(
    string Service,
    int TargetRtoMinutes,
    int TargetRpoMinutes,
    int ObservedRtoMinutes,
    int ObservedRpoMinutes,
    DateTimeOffset? LastSuccessfulBackup,
    DateTimeOffset? LastRestoreTest,
    bool RunbookHasOwner,
    bool DependenciesCovered,
    bool SmokeTestsPassed);
public sealed record DisasterRecoveryAssessment(int ReadinessScore, string Status, IReadOnlyList<OperationalFinding> Findings);

public sealed record AssetFinancialRequest(
    string AssetTag,
    decimal PurchaseCost,
    DateOnly PurchaseDate,
    int UsefulLifeMonths,
    decimal ResidualValue,
    decimal MaintenanceCost,
    DateOnly? WarrantyEnd,
    DateOnly? EndOfLife);
public sealed record AssetFinancialResult(
    decimal CurrentBookValue,
    decimal AccumulatedDepreciation,
    decimal TotalCostOfOwnership,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record LicensePositionRequest(
    string Product,
    string Metric,
    int Entitlements,
    int Consumed,
    int ActiveUsers,
    decimal UnitCost,
    DateOnly? RenewalDate);
public sealed record LicensePositionResult(
    int Balance,
    string Position,
    decimal AnnualCommittedCost,
    decimal AvoidableAnnualCost,
    IReadOnlyList<OperationalFinding> Findings);

public sealed record StructuredLogInput(
    DateTimeOffset Timestamp,
    string Severity,
    string Service,
    string Environment,
    string Message,
    string? TraceId,
    string? SpanId,
    IReadOnlyDictionary<string, object?>? Attributes);
public sealed record LogBatchValidationResult(
    IReadOnlyList<StructuredLogInput> Accepted,
    IReadOnlyList<OperationalFinding> Rejected,
    IReadOnlyDictionary<string, int> CountBySeverity);
