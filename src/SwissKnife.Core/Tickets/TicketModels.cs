using SwissKnife.Core.Entities;

namespace SwissKnife.Core.Tickets;

public sealed record CreateTicketCommand(
    TicketType Type,
    string Subject,
    string? Description,
    TicketPriority Priority,
    TicketImpact Impact,
    TicketUrgency Urgency,
    string? Category,
    string? Subcategory,
    string? RequesterEmail,
    Guid? AssigneeUserId,
    Guid? TeamOrgUnitId);

public sealed record UpdateTicketFieldsCommand(
    string Subject,
    string? Description,
    TicketPriority Priority,
    TicketImpact Impact,
    TicketUrgency Urgency,
    string? Category,
    string? Subcategory,
    Guid? AssigneeUserId,
    Guid? TeamOrgUnitId,
    string ExpectedConcurrencyStamp);

public sealed record TicketFilter(TicketType? Type, string? Status, TicketPriority? Priority, Guid? AssigneeUserId, bool IncludeBreachedOnly = false);

public sealed record TicketMetrics(
    int TotalOpen,
    int TotalResolved,
    int TotalClosed,
    double AverageAgingHoursOpen,
    double AverageResolutionHours,
    double SlaResponseComplianceRate,
    double SlaResolutionComplianceRate,
    double ReopenRate,
    IReadOnlyDictionary<string, int> VolumeByPriority);
