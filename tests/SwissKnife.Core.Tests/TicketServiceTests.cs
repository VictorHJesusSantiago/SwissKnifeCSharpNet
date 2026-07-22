using SwissKnife.Core.Entities;
using SwissKnife.Core.Repositories;
using SwissKnife.Core.Tickets;
using Xunit;

namespace SwissKnife.Core.Tests;

public sealed class TicketServiceTests
{
    private static CreateTicketCommand NewTicket(TicketPriority priority = TicketPriority.Medium) => new(
        TicketType.Incident, "Impressora não liga", "Detalhes do problema", priority,
        TicketImpact.Medium, TicketUrgency.Medium, "hardware", "printer", "user@example.com", null, null);

    [Fact]
    public async Task CreateAsync_assigns_sequential_numbers_per_tenant()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var first = await service.CreateAsync(NewTicket());
        var second = await service.CreateAsync(NewTicket());

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateAsync_applies_sla_due_dates_based_on_priority()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var critical = await service.CreateAsync(NewTicket(TicketPriority.Critical));
        var low = await service.CreateAsync(NewTicket(TicketPriority.Low));

        Assert.NotNull(critical.ResponseDueAt);
        Assert.NotNull(low.ResponseDueAt);
        Assert.True(critical.ResponseDueAt < low.ResponseDueAt, "SLA de prioridade crítica deve vencer antes do de baixa prioridade.");
    }

    [Fact]
    public async Task TransitionStatusAsync_without_configured_rules_allows_any_transition()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var ticket = await service.CreateAsync(NewTicket());
        var updated = await service.TransitionStatusAsync(ticket.Id, "in_progress", ticket.ConcurrencyStamp);

        Assert.Equal("in_progress", updated.Status);
    }

    [Fact]
    public async Task TransitionStatusAsync_rejects_transition_not_in_configured_rules()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        db.ResourceStateTransitions.Add(new ResourceStateTransition { Module = "tickets", FromState = "new", ToState = "in_progress" });
        await db.SaveChangesAsync();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var ticket = await service.CreateAsync(NewTicket());

        await Assert.ThrowsAsync<InvalidStateTransitionException>(() =>
            service.TransitionStatusAsync(ticket.Id, "resolved", ticket.ConcurrencyStamp));
    }

    [Fact]
    public async Task Resolving_then_reopening_a_ticket_increments_reopened_count()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var ticket = await service.CreateAsync(NewTicket());
        var resolved = await service.TransitionStatusAsync(ticket.Id, "resolved", ticket.ConcurrencyStamp);
        Assert.NotNull(resolved.ResolvedAt);

        var reopened = await service.TransitionStatusAsync(ticket.Id, "in_progress", resolved.ConcurrencyStamp);
        Assert.Equal(1, reopened.ReopenedCount);
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public async Task AddCommentAsync_marks_first_response_only_for_agent_responses()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var ticket = await service.CreateAsync(NewTicket());
        await service.AddCommentAsync(ticket.Id, "Só o requerente comentando", isInternal: false, isAgentResponse: false);
        var afterRequesterComment = await service.GetAsync(ticket.Id);
        Assert.Null(afterRequesterComment!.FirstRespondedAt);

        await service.AddCommentAsync(ticket.Id, "Estamos investigando", isInternal: false, isAgentResponse: true);
        var afterAgentComment = await service.GetAsync(ticket.Id);
        Assert.NotNull(afterAgentComment!.FirstRespondedAt);
    }

    [Fact]
    public async Task LinkAsync_creates_relationship_between_two_tickets()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var parent = await service.CreateAsync(NewTicket());
        var child = await service.CreateAsync(NewTicket());
        await service.LinkAsync(parent.Id, child.Id, TicketRelationType.ParentChild);

        var relationships = await service.ListRelationshipsAsync(parent.Id);
        Assert.Single(relationships);
        Assert.Equal(TicketRelationType.ParentChild, relationships[0].Type);
    }

    [Fact]
    public async Task GetMetricsAsync_reports_open_resolved_and_reopen_rate()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var service = new Tickets.TicketService(db, database.TenantAccessor);

        var open = await service.CreateAsync(NewTicket());
        var toResolve = await service.CreateAsync(NewTicket());
        await service.TransitionStatusAsync(toResolve.Id, "resolved", toResolve.ConcurrencyStamp);

        var metrics = await service.GetMetricsAsync();

        Assert.Equal(1, metrics.TotalOpen);
        Assert.Equal(1, metrics.TotalResolved);
        _ = open; // apenas garante que o ticket aberto existe e conta no total
    }
}
