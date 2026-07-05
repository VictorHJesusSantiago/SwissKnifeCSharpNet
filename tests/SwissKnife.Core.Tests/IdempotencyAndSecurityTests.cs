using SwissKnife.Core.Idempotency;
using SwissKnife.Core.Schema;
using SwissKnife.Core.Security;
using Xunit;

namespace SwissKnife.Core.Tests;

public sealed class IdempotencyAndSecurityTests
{
    [Fact]
    public async Task IdempotencyStore_replays_the_same_response_for_the_same_key_and_body()
    {
        using var database = new TestDatabase();
        var tenantId = Guid.NewGuid();
        await using var db = database.CreateContext();
        var store = new IdempotencyStore(db);

        await store.SaveAsync("key-1", tenantId, "/api/resources", "hash-abc", 201, """{"ok":true}""");
        var replayed = await store.TryGetAsync("key-1", tenantId, "/api/resources", "hash-abc");

        Assert.NotNull(replayed);
        Assert.Equal(201, replayed!.StatusCode);
    }

    [Fact]
    public async Task IdempotencyStore_rejects_key_reuse_with_a_different_request_body()
    {
        using var database = new TestDatabase();
        var tenantId = Guid.NewGuid();
        await using var db = database.CreateContext();
        var store = new IdempotencyStore(db);

        await store.SaveAsync("key-2", tenantId, "/api/resources", "hash-abc", 201, "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryGetAsync("key-2", tenantId, "/api/resources", "hash-different"));
    }

    [Fact]
    public void ModuleSchemaRegistry_falls_back_to_permissive_schema_for_unknown_modules()
    {
        var errors = ModuleSchemaRegistry.Validate("logs", """{"anything":"goes"}""");
        Assert.Empty(errors);
    }

    [Fact]
    public void ModuleSchemaRegistry_validates_required_fields_for_tickets()
    {
        var errors = ModuleSchemaRegistry.Validate("tickets", "{}");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void DataClassificationCatalog_flags_secret_fields_for_masking()
    {
        var fields = DataClassificationCatalog.FieldsAtOrAbove("vpn-profiles", SensitivityLevel.Confidential);
        Assert.Contains("gateway", fields);
    }
}
