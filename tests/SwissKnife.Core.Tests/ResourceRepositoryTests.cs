using SwissKnife.Core.Repositories;
using Xunit;

namespace SwissKnife.Core.Tests;

public sealed class ResourceRepositoryTests
{
    [Fact]
    public async Task CreateAsync_persists_and_validates_against_module_schema()
    {
        using var database = new TestDatabase();
        var tenantId = Guid.NewGuid();
        database.SetTenant(tenantId);
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        var created = await repository.CreateAsync(new CreateResourceCommand(
            "tickets", "Impressora não liga", "active", """{"type":"incident","priority":"high"}"""));

        Assert.Equal("Impressora não liga", created.Name);
        Assert.Equal(tenantId, created.TenantId);
    }

    [Fact]
    public async Task CreateAsync_rejects_payload_that_violates_module_schema()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        await Assert.ThrowsAsync<ResourceValidationException>(() => repository.CreateAsync(new CreateResourceCommand(
            "tickets", "Sem prioridade válida", "active", """{"type":"incident","priority":"nao-existe"}""")));
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_name_within_same_tenant_and_module()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        await repository.CreateAsync(new CreateResourceCommand("snippets", "meu-snippet", "active", "{}"));
        await Assert.ThrowsAsync<DuplicateResourceNameException>(() =>
            repository.CreateAsync(new CreateResourceCommand("snippets", "meu-snippet", "active", "{}")));
    }

    [Fact]
    public async Task UpdateAsync_throws_concurrency_conflict_when_stamp_is_stale()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        var created = await repository.CreateAsync(new CreateResourceCommand("snippets", "stamp-test", "active", "{}"));
        var staleStamp = created.ConcurrencyStamp;

        await repository.UpdateAsync(created.Id, new UpdateResourceCommand("stamp-test", "paused", "{}", staleStamp));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            repository.UpdateAsync(created.Id, new UpdateResourceCommand("stamp-test", "active", "{}", staleStamp)));
    }

    [Fact]
    public async Task SoftDelete_then_restore_brings_the_resource_back()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        var created = await repository.CreateAsync(new CreateResourceCommand("snippets", "trash-test", "active", "{}"));
        Assert.True(await repository.SoftDeleteAsync(created.Id));
        Assert.Null(await repository.GetAsync(created.Id));

        var restored = await repository.RestoreAsync(created.Id);
        Assert.NotNull(restored);
        Assert.NotNull(await repository.GetAsync(created.Id));
    }

    [Fact]
    public async Task Two_tenants_never_see_each_others_resources()
    {
        using var database = new TestDatabase();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        database.SetTenant(tenantA);
        var createdByA = await repository.CreateAsync(new CreateResourceCommand("snippets", "isolado-a", "active", "{}"));

        database.SetTenant(tenantB);
        var page = await repository.ListAsync(new ResourceFilter(), null, 25);
        Assert.DoesNotContain(page.Items, x => x.Id == createdByA.Id);
        Assert.Null(await repository.GetAsync(createdByA.Id));
    }

    [Fact]
    public async Task History_records_every_change_and_diff_reports_field_changes()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        var created = await repository.CreateAsync(new CreateResourceCommand("tickets", "diff-test", "active", """{"type":"incident","priority":"low"}"""));
        var updated = await repository.UpdateAsync(created.Id, new UpdateResourceCommand("diff-test", "active", """{"type":"incident","priority":"high"}""", created.ConcurrencyStamp));

        var history = await repository.GetHistoryAsync(created.Id);
        Assert.True(history.Count >= 2);

        var diff = await repository.DiffVersionsAsync(created.Id, 1, 2);
        Assert.Contains(diff, line => line.Contains("low"));
        Assert.Contains(diff, line => line.Contains("high"));
    }

    [Fact]
    public async Task RestoreVersionAsync_brings_back_an_old_payload_as_a_new_version()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        var created = await repository.CreateAsync(new CreateResourceCommand("tickets", "restore-version", "active", """{"type":"incident","priority":"low"}"""));
        await repository.UpdateAsync(created.Id, new UpdateResourceCommand("restore-version", "active", """{"type":"incident","priority":"high"}""", created.ConcurrencyStamp));

        var restored = await repository.RestoreVersionAsync(created.Id, 1);
        Assert.Contains("low", restored.PayloadJson);
    }

    [Fact]
    public async Task Resource_limits_reject_invalid_metadata()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);

        await Assert.ThrowsAsync<ResourceValidationException>(() => repository.CreateAsync(
            new("snippets", new string('x', 201), "status inválido!", "{}", ["ok"])));
        await Assert.ThrowsAsync<ResourceValidationException>(() => repository.CreateAsync(
            new("snippets", "nome", "active", "{}", Enumerable.Range(0, 51).Select(x => $"t{x}").ToArray())));
        await Assert.ThrowsAsync<ResourceValidationException>(() => repository.CreateAsync(
            new("snippets", "nome", "active", "{}", [new string('x', 65)])));
    }

    [Fact]
    public async Task Tags_and_metadata_are_normalized_and_update_cannot_duplicate_name()
    {
        using var database = new TestDatabase();
        database.SetTenant(Guid.NewGuid());
        await using var db = database.CreateContext();
        var repository = new ResourceRepository(db, database.TenantAccessor);
        var first = await repository.CreateAsync(new("SNIPPETS", "primeiro", "ACTIVE", "{}",
            [" CSharp ", "csharp", " API "], CostCenter: " CC-1 "));
        var second = await repository.CreateAsync(new("snippets", "segundo", "active", "{}"));

        Assert.Equal("snippets", first.Module);
        Assert.Equal("active", first.Status);
        Assert.Equal("CC-1", first.CostCenter);
        Assert.Equal(["api", "csharp"], first.Tags.Select(x => x.Tag));
        await Assert.ThrowsAsync<DuplicateResourceNameException>(() => repository.UpdateAsync(second.Id,
            new("primeiro", "active", "{}", second.ConcurrencyStamp)));
    }
}
