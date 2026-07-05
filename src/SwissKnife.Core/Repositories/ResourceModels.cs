namespace SwissKnife.Core.Repositories;

public sealed record ResourceFilter(
    string? Module = null,
    string? Status = null,
    string? Tag = null,
    string? Text = null,
    Guid? OwnerUserId = null,
    bool IncludeDeleted = false);

public sealed record ResourceSort(string Field = "UpdatedAt", bool Descending = true);

public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record CreateResourceCommand(
    string Module,
    string Name,
    string Status,
    string PayloadJson,
    IReadOnlyList<string>? Tags = null,
    Guid? OwnerUserId = null,
    Guid? TeamOrgUnitId = null,
    string? CostCenter = null,
    string? TemplateId = null);

public sealed record UpdateResourceCommand(
    string Name,
    string Status,
    string PayloadJson,
    string ExpectedConcurrencyStamp,
    IReadOnlyList<string>? Tags = null,
    Guid? OwnerUserId = null,
    string? CostCenter = null);
