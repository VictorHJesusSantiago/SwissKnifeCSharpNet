namespace SwissKnife.Core.Entities;

public enum TicketType { Incident, Request, Problem, Change }
public enum TicketPriority { Low, Medium, High, Critical }
public enum TicketImpact { Low, Medium, High }
public enum TicketUrgency { Low, Medium, High }

/// <summary>TKT-001..025: ticket como entidade de primeira classe (não mais um Resource
/// genérico) porque precisa de campos indexáveis para cálculo/consulta de SLA, numeração
/// sequencial e métricas — exatamente o caminho de evolução previsto na Fundação (FND-001)
/// para módulos que amadurecem além do CRUD genérico.</summary>
public sealed class TicketEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>TKT-002: numeração humana sequencial por tenant (ex.: INC-000123).</summary>
    public int Number { get; set; }

    public TicketType Type { get; set; }
    public string Subject { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "new";

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketImpact Impact { get; set; } = TicketImpact.Medium;
    public TicketUrgency Urgency { get; set; } = TicketUrgency.Medium;
    public string? Category { get; set; }
    public string? Subcategory { get; set; }

    public Guid? AssigneeUserId { get; set; }
    public Guid? TeamOrgUnitId { get; set; }
    public string? RequesterEmail { get; set; }

    // TKT-008/009/010: SLA de resposta e resolução.
    public DateTimeOffset? ResponseDueAt { get; set; }
    public DateTimeOffset? ResolutionDueAt { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public bool SlaResponseBreached { get; set; }
    public bool SlaResolutionBreached { get; set; }
    public bool SlaPaused { get; set; }

    // TKT-025: reabertura como sinal de qualidade de atendimento.
    public int ReopenedCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");

    public List<TicketWatcher> Watchers { get; set; } = [];
}

public sealed class TicketWatcher
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class TicketComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public string? AuthorName { get; set; }
    public required string Body { get; set; }
    public bool IsInternal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TicketRelationType { ParentChild, Duplicate, Blocks, RelatesTo }

/// <summary>TKT-013: vínculo pai/filho, duplicado, bloqueador e relacionado entre tickets.</summary>
public sealed class TicketRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceTicketId { get; set; }
    public Guid TargetTicketId { get; set; }
    public TicketRelationType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>TKT-008: política de SLA por prioridade (por tenant; nula = política global/padrão).</summary>
public sealed class TicketSlaPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public TicketPriority Priority { get; set; }
    public int ResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
}

public sealed class TicketNumberSequence
{
    public Guid TenantId { get; set; }
    public int LastNumber { get; set; }
}
